using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NitroGateway.Collection;
using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Measurements;
using NitroGateway.Shared;
using NitroGateway.Storage.Buffer;
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

        public int Count => Enqueued.Count;

        public Task<int> GetCountAsync(CancellationToken ct = default)
            => Task.FromResult(Enqueued.Count);

        public Task<OperationResult> EnqueueAsync(BatchMeasurements batch, CancellationToken ct = default)
        {
            Enqueued.Add(batch);
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
}
