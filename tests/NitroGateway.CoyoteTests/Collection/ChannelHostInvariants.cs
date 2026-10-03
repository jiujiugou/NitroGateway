using Microsoft.Extensions.Logging.Abstractions;
using NitroGateway.Collection;
using NitroGateway.Domain.Devices;
using NitroGateway.Shared;
using NitroGateway.Storage.TimeSeries;

namespace NitroGateway.CoyoteTests;

/// <summary>
/// MeasurementWriteHost 关停不变量（对应 notes/Invariants/collection-pipeline.md I3）：
/// I3 宿主停止后 <see cref="MeasurementWriteHost.Post"/> 必须被拒（返回 false），不得静默接收后丢弃。
/// <para>正例跑真实写宿主（修复后 StopAsync 会 <c>TryComplete</c>）；负控跑永远接收的坏宿主 → 断言变红。</para>
/// </summary>
internal static class ChannelHostInvariants
{
    public static Task I3_PostRejectedAfterStop_Positive()
    {
        var host = new MeasurementWriteHost(new NullStore(), NullLogger<MeasurementWriteHost>.Instance);
        return AssertPostRejectedAfterStop(
            () => host.StartAsync(default),
            () => host.StopAsync(default),
            () => host.Post([Snapshot()]));
    }

    /// <summary>负控：关停后仍接收 Post（静默丢弃）。</summary>
    public static Task I3_PostRejectedAfterStop_Negative_AcceptAlways()
    {
        var broken = new AlwaysAcceptHost();
        return AssertPostRejectedAfterStop(
            () => broken.StartAsync(default),
            () => broken.StopAsync(default),
            () => broken.Post([Snapshot()]));
    }

    private static async Task AssertPostRejectedAfterStop(Func<Task> start, Func<Task> stop, Func<bool> post)
    {
        await start();
        await stop();
        if (post())
            throw new InvalidOperationException("I3: 关停后 Post 仍被接收（数据静默丢弃）");
    }

    private static PointSnapshot Snapshot() => new()
    {
        DeviceId = Guid.NewGuid(),
        DevicePointId = Guid.NewGuid(),
        PointName = "P",
        DataType = DataType.Float,
        Value = 1.0,
        Timestamp = DateTime.UtcNow,
    };

    // ══════════════ 测试替身 ══════════════

    private sealed class NullStore : IMeasurementStore
    {
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
        public Task<OperationResult> PurgeAsync(DateTime before, CancellationToken ct = default)
            => Task.FromResult(OperationResult.Success());

        private static Task<OperationResult<IReadOnlyList<PointSnapshot>>> Empty()
            => Task.FromResult(OperationResult<IReadOnlyList<PointSnapshot>>.Success(Array.Empty<PointSnapshot>()));
    }

    /// <summary>坏宿主：停止后 Post 仍恒返回 true。</summary>
    private sealed class AlwaysAcceptHost
    {
        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
        public bool Post(IReadOnlyList<PointSnapshot> snapshots) => true;
    }
}
