using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NitroGateway.Collection;
using NitroGateway.Domain.Devices;
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
        public Task<OperationResult> WriteAsync(IReadOnlyList<PointSnapshot> snapshots, CancellationToken ct = default)
            => Task.FromResult(OperationResult.Success());

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
}
