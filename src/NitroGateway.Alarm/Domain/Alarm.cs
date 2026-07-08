namespace NitroGateway.Alarm.Domain;

/// <summary>
/// 告警记录。一条规则在一次超限事件中产生的告警实例。
/// 支持去重：同一规则在 Resolved 之前不会重复生成新告警。
/// </summary>
public sealed class Alarm
{
    /// <summary>告警唯一标识</summary>
    public Guid Id { get; init; }

    /// <summary>关联的规则 ID</summary>
    public required Guid RuleId { get; init; }

    /// <summary>所属设备 ID（冗余，便于查询）</summary>
    public required Guid DeviceId { get; init; }

    /// <summary>所属点位 ID（冗余，便于查询）</summary>
    public required Guid PointId { get; init; }

    /// <summary>触发时的值</summary>
    public double TriggerValue { get; init; }

    /// <summary>规则阈值</summary>
    public double Threshold { get; init; }

    /// <summary>告警严重等级</summary>
    public AlarmSeverity Severity { get; init; }

    /// <summary>告警消息（从 MessageTemplate 填充）</summary>
    public string Message { get; init; } = "";

    /// <summary>当前生命周期状态</summary>
    public AlarmState State { get; set; } = AlarmState.Active;

    /// <summary>首次超限时间（进入 Pending 的时间）</summary>
    public DateTime FirstExceededAt { get; init; }

    /// <summary>触发告警时间（变为 Active 的时间）</summary>
    public DateTime OccurredAt { get; init; }

    /// <summary>确认时间</summary>
    public DateTime? AcknowledgedAt { get; set; }

    /// <summary>恢复时间</summary>
    public DateTime? ResolvedAt { get; set; }
}
