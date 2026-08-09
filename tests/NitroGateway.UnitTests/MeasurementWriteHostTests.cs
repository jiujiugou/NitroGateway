using Microsoft.Extensions.Logging.Abstractions;
using NitroGateway.Collection;
using NitroGateway.Domain.Devices;
using NitroGateway.Shared;
using NitroGateway.Storage.TimeSeries;
using Xunit;

namespace NitroGateway.UnitTests;

public class MeasurementWriteHostTests
{
    /// <summary>前 N 次写入返回 Failure 结果的存储桩（区别于抛异常，验证 ADR-018 P2-1 的失败结果检查）</summary>
    private sealed class ResultFailingStore : IMeasurementStore
    {
        public int FailuresRemaining { get; set; } = 1;
        public List<PointSnapshot> Written { get; } = [];

        public Task<OperationResult> WriteAsync(IReadOnlyList<PointSnapshot> snapshots, CancellationToken ct = default)
        {
            if (FailuresRemaining > 0)
            {
                FailuresRemaining--;
                return Task.FromResult(OperationResult.Failure(OperationalError.DatabaseLocked("数据库锁定")));
            }
            Written.AddRange(snapshots);
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

    /// <summary>前 N 次写入抛异常的存储桩，之后正常记录</summary>
    private sealed class FlakyStore : IMeasurementStore
    {
        public int FailuresRemaining { get; set; } = 1;
        public List<PointSnapshot> Written { get; } = [];

        public Task<OperationResult> WriteAsync(IReadOnlyList<PointSnapshot> snapshots, CancellationToken ct = default)
        {
            if (FailuresRemaining > 0)
            {
                FailuresRemaining--;
                throw new InvalidOperationException("磁盘故障");
            }
            Written.AddRange(snapshots);
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

    [Fact]
    public async Task WriteFailure_IsIsolated_HostKeepsConsuming()
    {
        var store = new FlakyStore { FailuresRemaining = 1 };
        var host = new MeasurementWriteHost(store, NullLogger<MeasurementWriteHost>.Instance);

        await host.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(host.Post([MakeSnapshot(1)]), "第一批（将失败）应入队");
            Assert.True(host.Post([MakeSnapshot(2)]), "第二批（将成功）应入队");

            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (store.Written.Count == 0 && DateTime.UtcNow < deadline)
                await Task.Delay(20);

            Assert.Single(store.Written);
            Assert.Equal(2L, (long)store.Written[0].Value!);
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>ADR-018 P2-1：WriteAsync 返回 Failure（而非抛异常）时主机记告警并继续消费，不静默丢批次</summary>
    [Fact]
    public async Task WriteFailureResult_IsIsolated_HostKeepsConsuming()
    {
        var store = new ResultFailingStore { FailuresRemaining = 1 };
        var host = new MeasurementWriteHost(store, NullLogger<MeasurementWriteHost>.Instance);

        await host.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(host.Post([MakeSnapshot(1)]), "第一批（将失败）应入队");
            Assert.True(host.Post([MakeSnapshot(2)]), "第二批（将成功）应入队");

            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (store.Written.Count == 0 && DateTime.UtcNow < deadline)
                await Task.Delay(20);

            Assert.Single(store.Written);
            Assert.Equal(2L, (long)store.Written[0].Value!);
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }
    }

    private static PointSnapshot MakeSnapshot(long value) => new()
    {
        DeviceId = Guid.NewGuid(),
        DevicePointId = Guid.NewGuid(),
        PointName = "T1",
        Value = value,
        Timestamp = DateTime.UtcNow
    };
}
