using NitroGateway.Collection;
using NitroGateway.Domain.Devices;
using Xunit;

namespace NitroGateway.UnitTests;

/// <summary>
/// ChangeDetector（ADR-053 死区变化抑制）单测。
/// 放行语义：首样本必写 / 质量变化必写 / 心跳超时必写 /
/// |Δ| &lt; Deadband 抑制、≥ Deadband 写（含恰好等于）/ Deadband=0 全写 / Bool·String 值变化才写。
/// </summary>
public class ChangeDetectorTests
{
    /// <summary>基准时刻（UTC）；心跳判定用 nowUtc，与快照 Timestamp 无关。</summary>
    private static readonly DateTime T0 = new(2026, 8, 16, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>被测实例：心跳 300s（与默认配置一致）。</summary>
    private readonly ChangeDetector _detector = new(TimeSpan.FromSeconds(300));

    private static PointSnapshot Snap(
        Guid pointId, object? value,
        DataType type = DataType.Float, double deadband = 0,
        QualityCode quality = QualityCode.Good) => new()
    {
        DeviceId = Guid.NewGuid(),
        DevicePointId = pointId,
        PointName = "P",
        DataType = type,
        Value = value,
        Timestamp = T0,
        Quality = quality,
        Deadband = deadband
    };

    /// <summary>首样本必写（新点位 / 进程重启后首条即新基线）。</summary>
    [Fact]
    public void FirstSample_AlwaysPasses()
    {
        var passed = _detector.Filter([Snap(Guid.NewGuid(), 42.0, deadband: 100)], T0);
        Assert.Single(passed);
    }

    /// <summary>死区内（|Δ| &lt; Deadband）抑制不写。</summary>
    [Fact]
    public void WithinDeadband_Suppressed()
    {
        var pid = Guid.NewGuid();
        _detector.Filter([Snap(pid, 50.0, deadband: 1)], T0);
        var passed = _detector.Filter([Snap(pid, 50.5, deadband: 1)], T0.AddSeconds(1));
        Assert.Empty(passed);
    }

    /// <summary>恰好等于 Deadband 视为变化放行（与管线 Math.Abs(...)&lt;Deadband 抑制语义一致）。</summary>
    [Fact]
    public void EqualToDeadband_Passes()
    {
        var pid = Guid.NewGuid();
        _detector.Filter([Snap(pid, 50.0, deadband: 1)], T0);
        var passed = _detector.Filter([Snap(pid, 51.0, deadband: 1)], T0.AddSeconds(1));
        Assert.Single(passed);
    }

    /// <summary>超死区（|Δ| ≥ Deadband）放行。</summary>
    [Fact]
    public void OverDeadband_Passes()
    {
        var pid = Guid.NewGuid();
        _detector.Filter([Snap(pid, 50.0, deadband: 1)], T0);
        var passed = _detector.Filter([Snap(pid, 53.0, deadband: 1)], T0.AddSeconds(1));
        Assert.Single(passed);
    }

    /// <summary>Deadband=0：每样本都写（向后兼容，现有点位行为不变）。</summary>
    [Fact]
    public void ZeroDeadband_AlwaysPasses()
    {
        var pid = Guid.NewGuid();
        _detector.Filter([Snap(pid, 50.0)], T0);
        var passed = _detector.Filter([Snap(pid, 50.0001)], T0.AddSeconds(1));
        Assert.Single(passed);
    }

    /// <summary>质量变化必写（死区内也写，前端/告警据此看到掉线/恢复）。</summary>
    [Fact]
    public void QualityChange_PassesEvenWithinDeadband()
    {
        var pid = Guid.NewGuid();
        _detector.Filter([Snap(pid, 50.0, deadband: 1)], T0);
        var passed = _detector.Filter(
            [Snap(pid, 50.5, deadband: 1, quality: QualityCode.Bad)], T0.AddSeconds(1));
        Assert.Single(passed);
    }

    /// <summary>心跳超时即使值未变（死区内）也强制写一条（liveness 证据）。</summary>
    [Fact]
    public void HeartbeatElapsed_ForcesWriteEvenIfUnchanged()
    {
        var pid = Guid.NewGuid();
        _detector.Filter([Snap(pid, 50.0, deadband: 1)], T0);
        var passed = _detector.Filter([Snap(pid, 50.1, deadband: 1)], T0.AddSeconds(301));
        Assert.Single(passed);
    }

    /// <summary>进程重启 = 新实例无状态 → 首样本必写（无断档）。</summary>
    [Fact]
    public void NewInstance_Restart_FirstSamplePasses()
    {
        var pid = Guid.NewGuid();
        _detector.Filter([Snap(pid, 50.0, deadband: 1)], T0);
        var restarted = new ChangeDetector(TimeSpan.FromSeconds(300));
        var passed = restarted.Filter([Snap(pid, 50.0, deadband: 1)], T0.AddSeconds(10));
        Assert.Single(passed);
    }

    /// <summary>Bool：值未变抑制、值变化放行（无死区概念）。</summary>
    [Fact]
    public void Bool_UnchangedSuppressed_ChangedPasses()
    {
        var pid = Guid.NewGuid();
        _detector.Filter([Snap(pid, true, DataType.Bool)], T0);
        Assert.Empty(_detector.Filter([Snap(pid, true, DataType.Bool)], T0.AddSeconds(1)));
        Assert.Single(_detector.Filter([Snap(pid, false, DataType.Bool)], T0.AddSeconds(2)));
    }

    /// <summary>String：值未变抑制、值变化放行。</summary>
    [Fact]
    public void String_UnchangedSuppressed_ChangedPasses()
    {
        var pid = Guid.NewGuid();
        _detector.Filter([Snap(pid, "RUN", DataType.String)], T0);
        Assert.Empty(_detector.Filter([Snap(pid, "RUN", DataType.String)], T0.AddSeconds(1)));
        Assert.Single(_detector.Filter([Snap(pid, "STOP", DataType.String)], T0.AddSeconds(2)));
    }

    /// <summary>数值点位值无法转 double（如缩放失败 Value=null）→ 无法证明未变，保守放行（宁写勿丢）。</summary>
    [Fact]
    public void NonNumericValue_ConservativelyPasses()
    {
        var pid = Guid.NewGuid();
        _detector.Filter([Snap(pid, null, DataType.Float, deadband: 1)], T0);
        var passed = _detector.Filter([Snap(pid, null, DataType.Float, deadband: 1)], T0.AddSeconds(1));
        Assert.Single(passed);
    }

    /// <summary>心跳间隔必须为正数。</summary>
    [Fact]
    public void Constructor_RejectsNonPositiveHeartbeat()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ChangeDetector(TimeSpan.Zero));
    }
}
