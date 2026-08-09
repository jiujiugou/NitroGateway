using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NitroGateway.Collection;
using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Events;
using NitroGateway.Domain.Measurements;
using NitroGateway.Shared;
using NitroGateway.Storage.TimeSeries;
using Xunit;

namespace NitroGateway.UnitTests;

/// <summary>
/// ADR-016 P2-3：MeasurementWriteHost / SinkDispatcher 停机时排空 Channel 剩余项，
/// 与"优雅退出"注释一致，不再取消即丢。
/// </summary>
public class ChannelDrainTests
{
    // ═══════════ MeasurementWriteHost：主循环写操作带 stoppingToken，
    // 取消后必然落入排空分支（TryRead 剩余批次 + CancellationToken.None），可确定性验证 ═══════════

    [Fact]
    public async Task MeasurementWriteHost_OnStop_DrainsQueuedBatches()
    {
        var store = new GatedStore();
        var host = new MeasurementWriteHost(store, NullLogger<MeasurementWriteHost>.Instance);

        await host.StartAsync(CancellationToken.None);
        try
        {
            host.Post([MakeSnapshot(1)]);
            host.Post([MakeSnapshot(2)]);
            host.Post([MakeSnapshot(3)]);

            // 等消费者卡在第一批写入（Gate），其余批次留在队列
            await store.WaitFirstWriteStartedAsync();
            var stopTask = host.StopAsync(CancellationToken.None);

            // 让取消传播到主循环（第一批 WriteAsync 抛 OCE → 落入排空分支），再放行 Gate
            await Task.Delay(100);
            store.ReleaseGate();

            await stopTask;
            Assert.Equal(3, store.WriteCount);
        }
        finally
        {
            store.ReleaseGate();
        }
    }

    // ═══════════ SinkDispatcher：Sink 调用不带取消令牌，停止时剩余事件由
    // 主循环或排空分支送达均可，断言"停止前投递的事件不丢失"契约 ═══════════

    [Fact]
    public async Task SinkDispatcher_OnStop_DeliversQueuedEvents()
    {
        var sink = new GatedSink();
        var services = new ServiceCollection();
        services.AddSingleton<IPointStoredSink>(sink);
        await using var provider = services.BuildServiceProvider();

        var dispatcher = new SinkDispatcher(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<SinkDispatcher>.Instance);

        await dispatcher.StartAsync(CancellationToken.None);
        try
        {
            dispatcher.Post(new PointStoredEvent { DeviceId = Guid.NewGuid(), Snapshots = [] });
            dispatcher.Post(new PointStoredEvent { DeviceId = Guid.NewGuid(), Snapshots = [] });
            dispatcher.Post(new PointStoredEvent { DeviceId = Guid.NewGuid(), Snapshots = [] });

            await sink.WaitFirstStartedAsync();
            var stopTask = dispatcher.StopAsync(CancellationToken.None);

            await Task.Delay(100);
            sink.Release();

            await stopTask;
            Assert.Equal(3, sink.Count);
        }
        finally
        {
            sink.Release();
        }
    }

    private static PointSnapshot MakeSnapshot(double v) => new()
    {
        DeviceId = Guid.NewGuid(),
        DevicePointId = Guid.NewGuid(),
        DataType = DataType.Float,
        Value = v,
        Timestamp = DateTime.UtcNow,
        Quality = QualityCode.Good
    };

    /// <summary>首批写入卡 Gate，其余按序计数；Gate 放行后全部完成。</summary>
    private sealed class GatedStore : IMeasurementStore
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _firstWriteStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _writes;

        public int WriteCount => Volatile.Read(ref _writes);

        public Task WaitFirstWriteStartedAsync() => _firstWriteStarted.Task;
        public void ReleaseGate() => _gate.TrySetResult();

        public async Task<OperationResult> WriteAsync(
            IReadOnlyList<PointSnapshot> snapshots, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _writes) == 1)
                _firstWriteStarted.TrySetResult();
            await _gate.Task.WaitAsync(ct);
            return OperationResult.Success();
        }

        public Task<OperationResult<IReadOnlyList<PointSnapshot>>> QueryAsync(
            Guid deviceId, Guid pointId, DateTime from, DateTime to, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<OperationResult<IReadOnlyList<PointSnapshot>>> QueryByDeviceAsync(
            Guid deviceId, DateTime from, DateTime to, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<OperationResult<IReadOnlyList<PointSnapshot>>> QueryPagedAsync(
            Guid deviceId, Guid? pointId, DateTime from, DateTime to, int limit, int offset, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<OperationResult<IReadOnlyList<PointSnapshot>>> QueryLatestAsync(
            Guid deviceId, Guid? pointId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<OperationResult> PurgeAsync(DateTime before, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    /// <summary>首个事件卡 Gate，其余按序计数。</summary>
    private sealed class GatedSink : IPointStoredSink
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _firstStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public Task WaitFirstStartedAsync() => _firstStarted.Task;
        public void Release() => _gate.TrySetResult();

        public async ValueTask OnStoredAsync(PointStoredEvent e, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _count) == 1)
                _firstStarted.TrySetResult();
            await _gate.Task.WaitAsync(ct);
        }
    }
}