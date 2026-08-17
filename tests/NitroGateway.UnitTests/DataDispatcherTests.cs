using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NitroGateway.Collection;
using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Events;
using NitroGateway.Domain.Measurements;
using NitroGateway.Shared;
using NitroGateway.Storage.Buffer;
using NitroGateway.Storage.Disk;
using NitroGateway.Storage.TimeSeries;
using Xunit;

namespace NitroGateway.UnitTests;

/// <summary>
/// DataDispatcher 转发 payload 类型测试（ADR-001 P1-5）：
/// 快照携带的真实 DataType 必须透传到 MeasurementRecord，不再恒为 Float。
/// </summary>
public class DataDispatcherTests
{
    private sealed class FakeStore : IMeasurementStore
    {
        /// <summary>后台写入宿主实际落库的批次（按顺序捕获，ADR-053 验证只写放行子集）。</summary>
        public List<IReadOnlyList<PointSnapshot>> Written { get; } = [];

        public Task<OperationResult> WriteAsync(IReadOnlyList<PointSnapshot> snapshots, CancellationToken ct = default)
        {
            Written.Add(snapshots);
            return Task.FromResult(OperationResult.Success());
        }

        public Task<OperationResult<IReadOnlyList<PointSnapshot>>> QueryAsync(
            Guid deviceId, Guid pointId, DateTime from, DateTime to, CancellationToken ct = default)
            => Task.FromResult(OperationResult<IReadOnlyList<PointSnapshot>>.Success([]));

        public Task<OperationResult<IReadOnlyList<PointSnapshot>>> QueryByDeviceAsync(
            Guid deviceId, DateTime from, DateTime to, CancellationToken ct = default)
            => Task.FromResult(OperationResult<IReadOnlyList<PointSnapshot>>.Success([]));

        public Task<OperationResult<IReadOnlyList<PointSnapshot>>> QueryPagedAsync(
            Guid deviceId, Guid? pointId, DateTime from, DateTime to, int limit, int offset, CancellationToken ct = default)
            => Task.FromResult(OperationResult<IReadOnlyList<PointSnapshot>>.Success([]));

        public Task<OperationResult<IReadOnlyList<PointSnapshot>>> QueryLatestAsync(
            Guid deviceId, Guid? pointId, CancellationToken ct = default)
            => Task.FromResult(OperationResult<IReadOnlyList<PointSnapshot>>.Success([]));

        public Task<OperationResult> PurgeAsync(DateTime before, CancellationToken ct = default)
            => Task.FromResult(OperationResult.Success());
    }

    private sealed class FakeBuffer : IForwardBuffer
    {
        public List<BatchMeasurements> Enqueued { get; } = [];

        public List<(BatchMeasurements Batch, string Channel)> EnqueuedWithChannel { get; } = [];

        public int Count => Enqueued.Count;

        public Task<int> GetCountAsync(CancellationToken ct = default)
            => Task.FromResult(Enqueued.Count);

        public Task<OperationResult> EnqueueAsync(BatchMeasurements batch, CancellationToken ct = default)
        {
            Enqueued.Add(batch);
            return Task.FromResult(OperationResult.Success());
        }

        public Task<OperationResult> EnqueueAsync(BatchMeasurements batch, string channel, CancellationToken ct = default)
        {
            Enqueued.Add(batch);
            EnqueuedWithChannel.Add((batch, channel));
            return Task.FromResult(OperationResult.Success());
        }

        public Task<OperationResult<IReadOnlyList<BatchMeasurements>>> DequeueAsync(int maxCount, CancellationToken ct = default)
            => Task.FromResult(OperationResult<IReadOnlyList<BatchMeasurements>>.Success([]));

        public Task<OperationResult> CommitAsync(IReadOnlyList<Guid> batchIds, CancellationToken ct = default)
            => Task.FromResult(OperationResult.Success());

        public Task<OperationResult> MarkFailedAsync(Guid batchId, string reason, CancellationToken ct = default)
            => Task.FromResult(OperationResult.Success());

        public Task<OperationResult<IReadOnlyList<DeadLetterEntry>>> GetDeadLettersAsync(int maxCount, CancellationToken ct = default)
            => Task.FromResult(OperationResult<IReadOnlyList<DeadLetterEntry>>.Success([]));

        public Task<OperationResult> RetryDeadLetterAsync(Guid batchId, CancellationToken ct = default)
            => Task.FromResult(OperationResult.Success());

        public Task<OperationResult> DiscardDeadLetterAsync(Guid batchId, CancellationToken ct = default)
            => Task.FromResult(OperationResult.Success());

        public Task<OperationResult> PurgeDeadLettersAsync(DateTime before, CancellationToken ct = default)
            => Task.FromResult(OperationResult.Success());
    }

    /// <summary>ADR-012 测试用磁盘状态替身</summary>
    private sealed class FakeDiskStatus : IDiskStatus
    {
        public DiskLevel Level { get; set; }
        public event Action<DiskLevel>? Changed;
    }

    /// <summary>ADR-053 测试用事件接收替身：捕获 SinkDispatcher 推送的事件（FIFO 队列 + 计数）。</summary>
    private sealed class FakeSink : IPointStoredSink
    {
        private readonly System.Threading.Channels.Channel<PointStoredEvent> _events =
            System.Threading.Channels.Channel.CreateUnbounded<PointStoredEvent>();
        private int _totalEvents;

        /// <summary>已接收事件总数（供等待异步推送完成）。</summary>
        public int TotalEvents => Volatile.Read(ref _totalEvents);

        public ValueTask OnStoredAsync(PointStoredEvent e, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _totalEvents);
            _events.Writer.TryWrite(e);
            return ValueTask.CompletedTask;
        }

        /// <summary>等待并取出最早一个事件；超时抛 OperationCanceledException（避免测试悬挂）。</summary>
        public async Task<PointStoredEvent> WaitEventAsync(TimeSpan timeout)
            => await _events.Reader.ReadAsync(new CancellationTokenSource(timeout).Token);
    }

    /// <summary>ADR-012 P3：磁盘 Critical 时跳过时序写入与缓冲入队（降级保护磁盘），恢复后数据流自动恢复</summary>
    [Fact]
    public async Task DispatchAsync_DiskCritical_SkipsWriteAndEnqueue()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        await using var provider = services.BuildServiceProvider();

        var buffer = new FakeBuffer();
        var disk = new FakeDiskStatus { Level = DiskLevel.Critical };
        var dispatcher = new DataDispatcher(
            new MeasurementWriteHost(new FakeStore(), NullLogger<MeasurementWriteHost>.Instance),
            buffer,
            new SinkDispatcher(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<SinkDispatcher>.Instance),
            NullLogger<DataDispatcher>.Instance,
            disk);

        var result = await dispatcher.DispatchAsync(
            Guid.NewGuid(),
            [new PointSnapshot
            {
                DeviceId = Guid.NewGuid(),
                DevicePointId = Guid.NewGuid(),
                PointName = "P1",
                Value = 1.0,
                DataType = DataType.Float,
                Timestamp = DateTime.UtcNow,
                Quality = QualityCode.Good
            }],
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Empty(buffer.Enqueued);

        // 磁盘恢复后写入恢复正常
        disk.Level = DiskLevel.Healthy;
        var second = await dispatcher.DispatchAsync(
            Guid.NewGuid(),
            [new PointSnapshot
            {
                DeviceId = Guid.NewGuid(),
                DevicePointId = Guid.NewGuid(),
                PointName = "P2",
                Value = 2.0,
                DataType = DataType.Float,
                Timestamp = DateTime.UtcNow,
                Quality = QualityCode.Good
            }],
            CancellationToken.None);

        Assert.True(second.IsSuccess);
        Assert.Single(buffer.Enqueued);
    }

    /// <summary>ADR-011 P3：both 模式按通道各入队一行，且 batchId 独立（避免缓冲主键冲突）</summary>
    [Fact]
    public async Task DispatchAsync_BothChannels_EnqueuesPerChannel()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        await using var provider = services.BuildServiceProvider();

        var buffer = new FakeBuffer();
        var dispatcher = new DataDispatcher(
            new MeasurementWriteHost(new FakeStore(), NullLogger<MeasurementWriteHost>.Instance),
            buffer,
            new SinkDispatcher(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<SinkDispatcher>.Instance),
            NullLogger<DataDispatcher>.Instance,
            forwardChannels: [IForwardBuffer.MqttChannel, IForwardBuffer.HttpChannel]);

        var deviceId = Guid.NewGuid();
        var result = await dispatcher.DispatchAsync(
            deviceId,
            [new PointSnapshot
            {
                DeviceId = deviceId,
                DevicePointId = Guid.NewGuid(),
                PointName = "P1",
                Value = 1.0,
                DataType = DataType.Float,
                Timestamp = DateTime.UtcNow,
                Quality = QualityCode.Good
            }],
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, buffer.EnqueuedWithChannel.Count);
        Assert.Equal(
            [IForwardBuffer.MqttChannel, IForwardBuffer.HttpChannel],
            buffer.EnqueuedWithChannel.Select(e => e.Channel));
        Assert.NotEqual(
            buffer.EnqueuedWithChannel[0].Batch.Id,
            buffer.EnqueuedWithChannel[1].Batch.Id);
    }

    [Fact]
    public async Task DispatchAsync_PreservesPointDataType()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        await using var provider = services.BuildServiceProvider();

        var buffer = new FakeBuffer();
        var dispatcher = new DataDispatcher(
            new MeasurementWriteHost(new FakeStore(), NullLogger<MeasurementWriteHost>.Instance),
            buffer,
            new SinkDispatcher(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<SinkDispatcher>.Instance),
            NullLogger<DataDispatcher>.Instance);

        var deviceId = Guid.NewGuid();
        var snapshot = new PointSnapshot
        {
            DeviceId = deviceId,
            DevicePointId = Guid.NewGuid(),
            PointName = "B1",
            Value = true,
            DataType = DataType.Bool,
            Timestamp = DateTime.UtcNow,
            Quality = QualityCode.Good
        };

        var result = await dispatcher.DispatchAsync(deviceId, [snapshot], CancellationToken.None);

        Assert.True(result.IsSuccess);
        var record = Assert.Single(buffer.Enqueued[0].Records);
        Assert.Equal(DataType.Bool, record.DataType);
        Assert.Equal(true, record.Value);
    }

    /// <summary>ADR-016 P3-4：批次扫描窗口取快照时间戳 min/max，不再恒为分发时刻</summary>
    [Fact]
    public async Task DispatchAsync_BatchScanWindow_UsesSnapshotTimestamps()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        await using var provider = services.BuildServiceProvider();

        var buffer = new FakeBuffer();
        var dispatcher = new DataDispatcher(
            new MeasurementWriteHost(new FakeStore(), NullLogger<MeasurementWriteHost>.Instance),
            buffer,
            new SinkDispatcher(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<SinkDispatcher>.Instance),
            NullLogger<DataDispatcher>.Instance);

        var deviceId = Guid.NewGuid();
        var t1 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var t2 = t1.AddSeconds(1);
        var snapshots = new[]
        {
            new PointSnapshot
            {
                DeviceId = deviceId, DevicePointId = Guid.NewGuid(),
                DataType = DataType.Float, Value = 2d, Timestamp = t2, Quality = QualityCode.Good
            },
            new PointSnapshot
            {
                DeviceId = deviceId, DevicePointId = Guid.NewGuid(),
                DataType = DataType.Float, Value = 1d, Timestamp = t1, Quality = QualityCode.Good
            }
        };

        var result = await dispatcher.DispatchAsync(deviceId, snapshots, CancellationToken.None);

        Assert.True(result.IsSuccess);
        var batch = Assert.Single(buffer.Enqueued);
        Assert.Equal(t1, batch.ScanStartedAt);
        Assert.Equal(t2, batch.ScanCompletedAt);
    }

    // ─────────────────────── ADR-053 死区变化抑制（三处共用放行子集） ───────────────────────

    /// <summary>
    /// ADR-053 第一刀：启用 ChangeDetector 后，存储(SQLite)、转发(MQTT)、推送(SignalR)
    /// 三处共用同一个「放行子集」——只写/只转/只推变化点；事件仍收全量（桌面实时图/告警不受影响）。
    /// 断言：第二轮 P1 超死区放行、P2 死区内抑制 → 缓冲/落库都只含 P1，事件 Snapshots 全量 2 条
    /// 且 PersistedSnapshots 只含 P1。
    /// </summary>
    [Fact]
    public async Task DispatchAsync_WithChangeDetector_ThreePathsSharePassedSubset()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var sink = new FakeSink();
        services.AddSingleton<IPointStoredSink>(sink);
        await using var provider = services.BuildServiceProvider();

        var store = new FakeStore();
        var buffer = new FakeBuffer();
        var writeHost = new MeasurementWriteHost(store, NullLogger<MeasurementWriteHost>.Instance);
        var sinks = new SinkDispatcher(
            provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<SinkDispatcher>.Instance);
        var dispatcher = new DataDispatcher(
            writeHost,
            buffer,
            sinks,
            NullLogger<DataDispatcher>.Instance,
            changeDetector: new ChangeDetector(TimeSpan.FromMinutes(5)));

        try
        {
            await writeHost.StartAsync(CancellationToken.None);
            await sinks.StartAsync(CancellationToken.None);

            var deviceId = Guid.NewGuid();
            var p1 = Guid.NewGuid();
            var p2 = Guid.NewGuid();

            // 首轮：两个点都是首样本 → 全放行，建立基线
            await dispatcher.DispatchAsync(deviceId,
                [Snapshot(deviceId, p1, "P1", 10.0, 1.0), Snapshot(deviceId, p2, "P2", 10.0, 1.0)],
                CancellationToken.None);
            await WaitUntilAsync(() => store.Written.Count >= 1);
            await sink.WaitEventAsync(TimeSpan.FromSeconds(5));

            // 第二轮：P1 变化 10→11.5（Δ=1.5≥死区1.0）放行；P2 变化 10→10.2（Δ=0.2<死区）抑制
            await dispatcher.DispatchAsync(deviceId,
                [Snapshot(deviceId, p1, "P1", 11.5, 1.0), Snapshot(deviceId, p2, "P2", 10.2, 1.0)],
                CancellationToken.None);
            await WaitUntilAsync(() => store.Written.Count >= 2);
            var ev = await sink.WaitEventAsync(TimeSpan.FromSeconds(5));

            // 转发缓冲（MQTT）：第二轮只入队 P1
            var secondBatch = buffer.Enqueued[^1];
            var record = Assert.Single(secondBatch.Records);
            Assert.Equal(p1, record.DevicePointId);

            // 存储（SQLite）：第二轮只写 P1
            var secondWrite = store.Written[^1];
            Assert.Single(secondWrite, s => s.DevicePointId == p1);
            Assert.DoesNotContain(secondWrite, s => s.DevicePointId == p2);

            // 事件（SignalR 推送侧）：Snapshots 全量 2 条，PersistedSnapshots 只含 P1
            Assert.Equal(2, ev.Snapshots.Count);
            var persisted = Assert.Single(ev.PersistedSnapshots!);
            Assert.Equal(p1, persisted.DevicePointId);
        }
        finally
        {
            await writeHost.StopAsync(CancellationToken.None);
            await sinks.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// ADR-053：全部点在死区内 → 不写库、不入转发缓冲，但事件仍推送
    /// （Snapshots 全量、PersistedSnapshots 为空列表），SignalR 据此跳过推送，
    /// 桌面实时图/告警仍收全量。
    /// </summary>
    [Fact]
    public async Task DispatchAsync_AllSuppressed_NoStoreOrBufferWrite_EventStillPosted()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var sink = new FakeSink();
        services.AddSingleton<IPointStoredSink>(sink);
        await using var provider = services.BuildServiceProvider();

        var store = new FakeStore();
        var buffer = new FakeBuffer();
        var writeHost = new MeasurementWriteHost(store, NullLogger<MeasurementWriteHost>.Instance);
        var sinks = new SinkDispatcher(
            provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<SinkDispatcher>.Instance);
        var dispatcher = new DataDispatcher(
            writeHost,
            buffer,
            sinks,
            NullLogger<DataDispatcher>.Instance,
            changeDetector: new ChangeDetector(TimeSpan.FromMinutes(5)));

        try
        {
            await writeHost.StartAsync(CancellationToken.None);
            await sinks.StartAsync(CancellationToken.None);

            var deviceId = Guid.NewGuid();
            var p1 = Guid.NewGuid();
            var p2 = Guid.NewGuid();

            // 首轮建立基线（全放行）
            await dispatcher.DispatchAsync(deviceId,
                [Snapshot(deviceId, p1, "P1", 10.0, 1.0), Snapshot(deviceId, p2, "P2", 10.0, 1.0)],
                CancellationToken.None);
            await WaitUntilAsync(() => store.Written.Count >= 1);
            await sink.WaitEventAsync(TimeSpan.FromSeconds(5));

            // 第二轮：都在死区内（Δ<1.0）→ 全部抑制
            await dispatcher.DispatchAsync(deviceId,
                [Snapshot(deviceId, p1, "P1", 10.3, 1.0), Snapshot(deviceId, p2, "P2", 10.4, 1.0)],
                CancellationToken.None);
            await WaitUntilAsync(() => sink.TotalEvents >= 2);

            // 无新增缓冲入队（仍只有首轮 1 批）、无新增落库（仍只有首轮 1 批）
            Assert.Single(buffer.Enqueued);
            Assert.Single(store.Written);

            // 事件仍推送：Snapshots 全量、PersistedSnapshots 为空列表（SignalR 据此跳过）
            var ev = await sink.WaitEventAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, ev.Snapshots.Count);
            Assert.NotNull(ev.PersistedSnapshots);
            Assert.Empty(ev.PersistedSnapshots!);
        }
        finally
        {
            await writeHost.StopAsync(CancellationToken.None);
            await sinks.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// ADR-053 兼容回归：不传 ChangeDetector（旧调用方/独立测试）时行为不变——
    /// 全量写库/转发，事件 PersistedSnapshots 回退全量（SignalR 推送不受影响）。
    /// </summary>
    [Fact]
    public async Task DispatchAsync_WithoutChangeDetector_FullSetPassedThrough()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var sink = new FakeSink();
        services.AddSingleton<IPointStoredSink>(sink);
        await using var provider = services.BuildServiceProvider();

        var store = new FakeStore();
        var buffer = new FakeBuffer();
        var writeHost = new MeasurementWriteHost(store, NullLogger<MeasurementWriteHost>.Instance);
        var sinks = new SinkDispatcher(
            provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<SinkDispatcher>.Instance);
        // 不传 changeDetector：保持旧行为（null → 回退全量）
        var dispatcher = new DataDispatcher(
            writeHost, buffer, sinks, NullLogger<DataDispatcher>.Instance);

        try
        {
            await writeHost.StartAsync(CancellationToken.None);
            await sinks.StartAsync(CancellationToken.None);

            var deviceId = Guid.NewGuid();
            var p1 = Guid.NewGuid();
            var p2 = Guid.NewGuid();
            await dispatcher.DispatchAsync(deviceId,
                [Snapshot(deviceId, p1, "P1", 1.0, 0), Snapshot(deviceId, p2, "P2", 2.0, 0)],
                CancellationToken.None);

            await WaitUntilAsync(() => store.Written.Count >= 1);
            var ev = await sink.WaitEventAsync(TimeSpan.FromSeconds(5));

            Assert.Single(buffer.Enqueued);
            Assert.Equal(2, buffer.Enqueued[0].Records.Count);
            Assert.Equal(2, store.Written[0].Count);
            Assert.NotNull(ev.PersistedSnapshots);
            Assert.Equal(2, ev.PersistedSnapshots!.Count);
            Assert.Equal(2, ev.Snapshots.Count);
        }
        finally
        {
            await writeHost.StopAsync(CancellationToken.None);
            await sinks.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>构造数值点位快照（Deadband 透传，ADR-053）。</summary>
    private static PointSnapshot Snapshot(Guid deviceId, Guid pointId, string name, double value, double deadband) => new()
    {
        DeviceId = deviceId,
        DevicePointId = pointId,
        PointName = name,
        DataType = DataType.Float,
        Value = value,
        Deadband = deadband,
        Timestamp = DateTime.UtcNow,
        Quality = QualityCode.Good
    };

    /// <summary>轮询等待异步条件满足（后台服务写入/事件推送为异步，需小等，避免测试悬挂）。</summary>
    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("等待异步条件超时");
            await Task.Delay(20);
        }
    }
}
