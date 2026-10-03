using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NitroGateway.Collection;
using NitroGateway.Domain.Devices;
using NitroGateway.Host;

namespace NitroGateway.CoyoteTests;

/// <summary>
/// CollectionEngine 关停不变量（对应 notes/Invariants/collection-pipeline.md I1）：
/// I1 停后不再启新轮——<c>StopAsync</c> 返回后引擎已静止，不再启动采集轮。
/// <para>正例跑真实 <see cref="CollectionEngine"/>（经假 scope 工厂注入假采集器）；
/// 负控跑 <see cref="BrokenEngine"/>（忽略停止令牌、StopAsync 不等待不取消）——同一断言必须变红。
/// 坏实现的循环在断言后必须被完全 join，避免遗留受控任务污染后续用例。</para>
/// <para>在途轮收敛（I2）未做检测器：真实引擎的 30s `Task.Delay` 超时路径在 Coyote 受控时间下
/// 于部分调度被判 deadlock（非真实缺陷），见 collection-pipeline.md X6。</para>
/// </summary>
internal static class CollectionEngineInvariants
{
    public static Task I1_NoNewRoundAfterStop_Positive()
    {
        var collector = new FakeCollector();
        var engine = RealEngine(collector, new GatewayLifecycle());
        return RunStopQuiescence(engine, collector, null);
    }

    /// <summary>负控：StopAsync 不真正停止 → 停后继续启动采集轮。</summary>
    public static Task I1_NoNewRoundAfterStop_Negative_IgnoringStop()
    {
        var collector = new FakeCollector();
        var engine = new BrokenEngine(collector);
        return RunStopQuiescence(engine, collector, async () =>
        {
            engine.StopLoop();
            await engine.RunTask;
        });
    }

    private static async Task RunStopQuiescence(IHostedService engine, FakeCollector collector, Func<Task>? quiesce)
    {
        await engine.StartAsync(default);
        while (collector.Calls == 0) await Task.Yield();

        await engine.StopAsync(default);
        var afterStop = collector.Calls;

        for (var i = 0; i < 5; i++) await Task.Yield();
        var afterGrowth = collector.Calls;
        if (quiesce is not null) await quiesce();

        if (afterGrowth != afterStop)
            throw new InvalidOperationException(
                $"I1: StopAsync 返回后仍启动新采集轮：{afterStop} → {afterGrowth}");
    }

    private static CollectionEngine RealEngine(FakeCollector collector, GatewayLifecycle lifecycle)
        => new(
            new FakeScopeFactory(collector),
            lifecycle,
            Options.Create(new CollectionOption { IntervalMs = 5 }),
            NullLogger<CollectionEngine>.Instance,
            errorRetryDelay: TimeSpan.FromMilliseconds(1));

    // ══════════════ 测试替身 ══════════════

    /// <summary>假采集器：计数调用（每轮完成一次受控 yield，不依赖计时器）。</summary>
    private sealed class FakeCollector : IDeviceCollector
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public async Task CollectOnceAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            await Task.Yield();
        }

        public Task CollectDeviceAsync(Device device, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeScopeFactory(FakeCollector collector) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new FakeScope(collector);

        private sealed class FakeScope(FakeCollector c) : IServiceScope
        {
            public IServiceProvider ServiceProvider { get; } = new FakeProvider(c);
            public void Dispose() { }
        }

        private sealed class FakeProvider(FakeCollector c) : IServiceProvider
        {
            public object? GetService(Type serviceType)
                => serviceType == typeof(IDeviceCollector) ? c : null;
        }
    }

    // ══════════════ 坏实现（negative-control 种子） ══════════════

    /// <summary>忽略停止令牌的坏引擎：StopAsync 立即返回、也不取消在途轮。</summary>
    private sealed class BrokenEngine(FakeCollector collector) : BackgroundService
    {
        private volatile bool _loop = true;
        private Task? _runTask;

        public Task RunTask => _runTask ?? Task.CompletedTask;

        public void StopLoop() => _loop = false;

        protected override Task ExecuteAsync(CancellationToken stoppingToken)
            => _runTask = RunLoopAsync();

        private async Task RunLoopAsync()
        {
            while (_loop)
            {
                await collector.CollectOnceAsync(CancellationToken.None);  // 坏：无视宿主停止令牌
                await Task.Yield();
            }
        }

        public override Task StopAsync(CancellationToken cancellationToken)
            => Task.CompletedTask;   // 坏：不排水、不取消
    }
}
