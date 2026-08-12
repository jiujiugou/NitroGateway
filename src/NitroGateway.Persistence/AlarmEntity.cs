namespace NitroGateway.Persistence;

/// <summary>
/// 告警记录 EF 实体，映射 alarms 表（snake_case 列名与 M005 迁移一致）。
/// 时间与枚举均以字符串存储（O 格式 / ToString），与既有数据格式保持一致。
/// </summary>
public sealed class AlarmEntity
{
    /// <summary>告警 ID（Guid 字符串）</summary>
    public required string Id { get; set; }

    /// <summary>关联规则 ID（Guid 字符串）</summary>
    public required string RuleId { get; set; }

    /// <summary>设备 ID（Guid 字符串）</summary>
    public required string DeviceId { get; set; }

    /// <summary>点位 ID（Guid 字符串）</summary>
    public required string PointId { get; set; }

    /// <summary>触发值</summary>
    public double? TriggerValue { get; set; }

    /// <summary>阈值</summary>
    public double? Threshold { get; set; }

    /// <summary>严重等级（AlarmSeverity 字符串）</summary>
    public required string Severity { get; set; }

    /// <summary>告警消息</summary>
    public string Message { get; set; } = "";

    /// <summary>生命周期状态（AlarmState 字符串）</summary>
    public required string State { get; set; }

    /// <summary>首次超限时间（O 格式字符串）</summary>
    public string? FirstExceededAt { get; set; }

    /// <summary>触发时间（O 格式字符串）</summary>
    public required string OccurredAt { get; set; }

    /// <summary>确认时间（O 格式字符串）</summary>
    public string? AcknowledgedAt { get; set; }

    /// <summary>恢复时间（O 格式字符串）</summary>
    public string? ResolvedAt { get; set; }

    /// <summary>站点标识（ADR-035 第 1 步）：Ingest 按上行 topic 第三层写入；空串=未标注站点</summary>
    public string SiteId { get; set; } = "";
}
