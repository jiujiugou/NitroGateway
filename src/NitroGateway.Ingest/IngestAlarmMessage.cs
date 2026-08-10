namespace NitroGateway.Ingest;

/// <summary>
/// 告警上行消息（与 <c>MqttAlarmNotifier</c> 发布契约一致：camelCase JSON，QoS1）。
/// 现场侧在告警状态迁移（Pending/Active/Acknowledged/Resolved）时各推送一次，
/// 中心按 <see cref="AlarmId"/> UPSERT 到 alarms 表（ADR-028 P2-1 契约对齐）。
/// </summary>
public sealed record IngestAlarmMessage
{
    /// <summary>告警唯一标识（中心 alarms.id 主键）</summary>
    public Guid AlarmId { get; init; }

    /// <summary>触发规则 ID</summary>
    public Guid RuleId { get; init; }

    /// <summary>所属设备 ID</summary>
    public Guid DeviceId { get; init; }

    /// <summary>所属点位 ID</summary>
    public Guid PointId { get; init; }

    /// <summary>触发时的值（可空，兼容未知）</summary>
    public double? TriggerValue { get; init; }

    /// <summary>规则阈值（可空，兼容未知）</summary>
    public double? Threshold { get; init; }

    /// <summary>严重等级（AlarmSeverity 名称字符串）</summary>
    public string Severity { get; init; } = "";

    /// <summary>告警消息</summary>
    public string Message { get; init; } = "";

    /// <summary>生命周期状态（AlarmState 名称字符串）</summary>
    public string State { get; init; } = "";

    /// <summary>触发时间（UTC O 格式入库）</summary>
    public DateTime OccurredAt { get; init; }
}
