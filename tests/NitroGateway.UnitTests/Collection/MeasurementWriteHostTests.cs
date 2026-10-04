using Microsoft.Extensions.Logging.Abstractions;
using NitroGateway.Collection;
using NitroGateway.Desktop.Services.Infrastructure;
using NitroGateway.Domain.Devices;
using NitroGateway.Shared;
using NitroGateway.Storage.TimeSeries;
using Xunit;

namespace NitroGateway.UnitTests.Collection;

public class MeasurementWriteHostTests
{
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

    [Fact]
    public async Task ChannelOverflow_DroppedPointsAreCounted()
    {
        // 不启动后台消费，令有界通道（容量 1000）溢出；DropOldest 静默丢弃必须被指标可见化。
        var host = new MeasurementWriteHost(new ResultFailingStore(), NullLogger<MeasurementWriteHost>.Instance);
        var source = new PrometheusMetricsSource();

        var before = MetricsSnapshot.Parse(await source.ScrapeAsync())
            .Value("nitro_store_channel_dropped_points_total") ?? 0;

        for (var i = 0; i < 1002; i++)
            host.Post([MakeSnapshot(i)]);

        var after = MetricsSnapshot.Parse(await source.ScrapeAsync())
            .Value("nitro_store_channel_dropped_points_total") ?? 0;

        Assert.True(after >= before + 2, $"期望至少丢弃 2 点，before={before} after={after}");
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
