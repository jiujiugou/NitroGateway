using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NitroGateway.Domain.Devices;
using NitroGateway.Persistence.Sqlite;
using NitroGateway.Shared;
using NitroGateway.Storage.TimeSeries;

namespace NitroGateway.CoyoteTests;

/// <summary>
/// MeasurementRetentionService 不变量（对应 notes/Invariants/collection-pipeline.md I4）：
/// I4 清理 cutoff 恒为 <c>now - retentionDays</c>，且停机取消后不再执行清理。
/// <para>正例跑真实保留服务（注入记录型假存储）；负控跑 cutoff=now 的坏服务 → 断言变红。</para>
/// </summary>
internal static class RetentionInvariants
{
    private const int RetentionDays = 30;

    public static Task I4_CutoffAndCancellation_Positive()
        => RunCutoff(store => new MeasurementRetentionService(
            store, NullLogger<MeasurementRetentionService>.Instance,
            retentionDays: RetentionDays, interval: TimeSpan.FromHours(1)));

    /// <summary>负控：用 now 作为 cutoff（删除全部历史）→ 断言截止时间出错。</summary>
    public static Task I4_CutoffAndCancellation_Negative_NowCutoff()
        => RunCutoff(store => new BrokenRetentionService(store));

    private static async Task RunCutoff(Func<IMeasurementStore, IHostedService> build)
    {
        var store = new RecordingStore();
        var service = build(store);

        await service.StartAsync(default);

        var spins = 0;
        while (store.PurgeCount == 0 && spins++ < 1000) await Task.Yield();
        if (store.PurgeCount == 0)
            throw new InvalidOperationException("I4: 保留服务未执行清理");

        var before = store.FirstPurge;
        var lower = DateTime.UtcNow.AddDays(-RetentionDays).AddMinutes(-2);
        var upper = DateTime.UtcNow.AddDays(-RetentionDays).AddMinutes(2);
        if (before < lower || before > upper)
            throw new InvalidOperationException(
                $"I4: 保留 cutoff 应为 now-{RetentionDays}d，实际 {before:O}");

        await service.StopAsync(default);
        var afterStop = store.PurgeCount;
        for (var i = 0; i < 5; i++) await Task.Yield();
        if (store.PurgeCount != afterStop)
            throw new InvalidOperationException("I4: 停机后仍执行清理");
    }

    // ══════════════ 测试替身 ══════════════

    private sealed class RecordingStore : IMeasurementStore
    {
        private int _purgeCount;
        private long _firstPurgeTicks;

        public int PurgeCount => Volatile.Read(ref _purgeCount);
        public DateTime FirstPurge => new(Interlocked.Read(ref _firstPurgeTicks), DateTimeKind.Utc);

        public Task<OperationResult> PurgeAsync(DateTime before, CancellationToken ct = default)
        {
            // 先记录首个 cutoff 再自增计数：确保观察者看到 PurgeCount>0 时 cutoff 已可见。
            Interlocked.CompareExchange(ref _firstPurgeTicks, before.Ticks, 0L);
            Interlocked.Increment(ref _purgeCount);
            return Task.FromResult(OperationResult.Success());
        }

        public Task<OperationResult> WriteAsync(IReadOnlyList<PointSnapshot> snapshots, CancellationToken ct = default)
            => Task.FromResult(OperationResult.Success());
        public Task<OperationResult<IReadOnlyList<PointSnapshot>>> QueryAsync(
            Guid deviceId, Guid pointId, DateTime from, DateTime to, CancellationToken ct = default)
            => Empty();
        public Task<OperationResult<IReadOnlyList<PointSnapshot>>> QueryByDeviceAsync(
            Guid deviceId, DateTime from, DateTime to, CancellationToken ct = default)
            => Empty();
        public Task<OperationResult<IReadOnlyList<PointSnapshot>>> QueryPagedAsync(
            Guid deviceId, Guid? pointId, DateTime from, DateTime to, int limit, int offset, CancellationToken ct = default)
            => Empty();
        public Task<OperationResult<IReadOnlyList<PointSnapshot>>> QueryLatestAsync(
            Guid deviceId, Guid? pointId, CancellationToken ct = default)
            => Empty();

        private static Task<OperationResult<IReadOnlyList<PointSnapshot>>> Empty()
            => Task.FromResult(OperationResult<IReadOnlyList<PointSnapshot>>.Success(Array.Empty<PointSnapshot>()));
    }

    // ══════════════ 坏实现（negative-control 种子） ══════════════

    /// <summary>坏保留服务：用 now 当 cutoff（等价于删除所有历史）。</summary>
    private sealed class BrokenRetentionService(IMeasurementStore store) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await store.PurgeAsync(DateTime.UtcNow, stoppingToken);   // 坏：cutoff=now
            try { await Task.Delay(Timeout.Infinite, stoppingToken); }
            catch (OperationCanceledException) { }
        }
    }
}
