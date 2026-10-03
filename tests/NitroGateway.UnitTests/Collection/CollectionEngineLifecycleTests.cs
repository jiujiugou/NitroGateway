using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NitroGateway.Collection;
using NitroGateway.Domain.Devices;
using NitroGateway.Host;
using Xunit;

namespace NitroGateway.UnitTests.Collection;

/// <summary>
/// 采集引擎生命周期路径（变异覆盖补齐）：
/// 每 tick 执行一轮 / 单轮异常后重试 / 停止等待在途轮 / 停止超时取消在途轮。
/// 全部使用确定性同步（TCS/Gate），不使用计时等待驱动断言。
/// </summary>
public class CollectionEngineLifecycleTests
{
    private sealed class CountingCollector : IDeviceCollector
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public Task CollectDeviceAsync(Device device, CancellationToken ct) => Task.CompletedTask;
        public Task CollectOnceAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            return Task.CompletedTask;
        }
    }

    private sealed class FlakyCollector : IDeviceCollector
    {
        private int _calls;
        public int FailuresRemaining { get; set; } = 1;
        public int Calls => Volatile.Read(ref _calls);
        public Task CollectDeviceAsync(Device device, CancellationToken ct) => Task.CompletedTask;
        public Task CollectOnceAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            if (FailuresRemaining > 0)
            {
                FailuresRemaining--;
                throw new InvalidOperationException("采集器故障");
            }
            return Task.CompletedTask;
        }
    }

    /// <summary>轮次阻塞在 Gate 上，直到 Release 或令牌取消。</summary>
    private sealed class BlockingCollector : IDeviceCollector
    {
        private readonly TaskCompletionSource _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;
        public volatile bool RoundCompleted;
        public volatile bool Cancelled;
        public void Release() => _release.TrySetResult();

        public Task CollectDeviceAsync(Device device, CancellationToken ct) => Task.CompletedTask;

        public async Task CollectOnceAsync(CancellationToken ct)
        {
            _started.TrySetResult();
            try
            {
                await _release.Task.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                Cancelled = true;
                throw;
            }
            RoundCompleted = true;
        }
    }

    private static (CollectionEngine Engine, ServiceProvider Provider) BuildEngine(
        IDeviceCollector collector, int intervalMs, TimeSpan? retryDelay = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(collector);
        var provider = services.BuildServiceProvider();
        var engine = new CollectionEngine(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new GatewayLifecycle(),
            Options.Create(new CollectionOption { IntervalMs = intervalMs }),
            NullLogger<CollectionEngine>.Instance,
            retryDelay);
        return (engine, provider);
    }

    [Fact]
    public async Task Execute_RunsRoundForEachTick()
    {
        var collector = new CountingCollector();
        var (engine, provider) = BuildEngine(collector, intervalMs: 20);
        await using (provider)
        {
            await engine.StartAsync(CancellationToken.None);
            try
            {
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (collector.Calls < 2 && DateTime.UtcNow < deadline)
                    await Task.Delay(10);

                Assert.True(collector.Calls >= 2, $"引擎应按 tick 多次执行采集，实际 {collector.Calls} 次");
            }
            finally
            {
                await engine.StopAsync(CancellationToken.None);
            }
        }
    }

    [Fact]
    public async Task Execute_RoundThrows_RetriesAfterInjectedDelay()
    {
        var collector = new FlakyCollector { FailuresRemaining = 1 };
        var (engine, provider) = BuildEngine(
            collector, intervalMs: 20, retryDelay: TimeSpan.FromMilliseconds(10));
        await using (provider)
        {
            await engine.StartAsync(CancellationToken.None);
            try
            {
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (collector.Calls < 2 && DateTime.UtcNow < deadline)
                    await Task.Delay(10);

                Assert.True(collector.Calls >= 2, $"单轮异常后应延迟重试，实际 {collector.Calls} 次");
            }
            finally
            {
                await engine.StopAsync(CancellationToken.None);
            }
        }
    }

    [Fact]
    public async Task Stop_WhileRoundInFlight_WaitsForRoundToFinish()
    {
        var collector = new BlockingCollector();
        var (engine, provider) = BuildEngine(collector, intervalMs: 20);
        await using (provider)
        {
            await engine.StartAsync(CancellationToken.None);
            await collector.Started;       // 轮次已启动
            await Task.Delay(50);          // 确保 _currentRound 已赋值

            var stopTask = engine.StopAsync(CancellationToken.None);
            await Task.Delay(50);
            Assert.False(stopTask.IsCompleted, "在途轮未完成前 Stop 不应返回");

            collector.Release();
            await stopTask;

            Assert.True(collector.RoundCompleted, "Stop 返回前在途轮必须已完成");
        }
    }

    [Fact]
    public async Task Stop_Timeout_CancelsInFlightRound()
    {
        var collector = new BlockingCollector();
        var (engine, provider) = BuildEngine(collector, intervalMs: 20);
        await using (provider)
        {
            await engine.StartAsync(CancellationToken.None);
            await collector.Started;
            await Task.Delay(50);

            // 已取消的令牌使 StopAsync 的等待立即超时 → 走"取消在途轮"分支
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await engine.StopAsync(cts.Token).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(collector.Cancelled, "停止超时应取消在途轮的 CancellationToken");
        }
    }
}
