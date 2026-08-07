using Microsoft.Extensions.Logging.Abstractions;
using NitroGateway.Domain.Devices;
using NitroGateway.Persistence.Sqlite;
using NitroGateway.Shared;
using NitroGateway.Storage.TimeSeries;
using Xunit;

namespace NitroGateway.UnitTests;

/// <summary>MeasurementRetentionService 测试（ADR-002 P1-2）：周期清理阈值正确、失败不中断。</summary>
public class MeasurementRetentionServiceTests
{
    /// <summary>记录每次 PurgeAsync 阈值，前 N 次可注入失败。</summary>
    private sealed class RecordingStore : IMeasurementStore
    {
        public List<DateTime> PurgeBefore { get; } = [];
        public int FailuresRemaining { get; set; }

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
        {
            if (FailuresRemaining > 0)
            {
                FailuresRemaining--;
                return Task.FromResult(OperationResult.Failure(OperationalError.Storage("磁盘故障")));
            }

            PurgeBefore.Add(before);
            return Task.FromResult(OperationResult.Success());
        }
    }

    [Fact]
    public async Task ExecuteAsync_PurgesPeriodically_WithRetentionThreshold()
    {
        var store = new RecordingStore();
        var service = new MeasurementRetentionService(
            store,
            NullLogger<MeasurementRetentionService>.Instance,
            retentionDays: 30,
            interval: TimeSpan.FromMilliseconds(50));

        await service.StartAsync(CancellationToken.None);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (store.PurgeBefore.Count < 2 && DateTime.UtcNow < deadline)
                await Task.Delay(20);

            Assert.True(store.PurgeBefore.Count >= 2, "服务应按周期重复调用 PurgeAsync");
            var expected = DateTime.UtcNow.AddDays(-30);
            foreach (var before in store.PurgeBefore)
            {
                Assert.True((expected - before).Duration() < TimeSpan.FromMinutes(1), "清理阈值应约为 now-30 天");
            }
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task PurgeFailure_DoesNotStopService()
    {
        var store = new RecordingStore { FailuresRemaining = 1 };
        var service = new MeasurementRetentionService(
            store,
            NullLogger<MeasurementRetentionService>.Instance,
            retentionDays: 7,
            interval: TimeSpan.FromMilliseconds(50));

        await service.StartAsync(CancellationToken.None);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (store.PurgeBefore.Count < 1 && DateTime.UtcNow < deadline)
                await Task.Delay(20);

            Assert.True(store.PurgeBefore.Count >= 1, "首次失败后下个周期应重试成功");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }
}
