namespace NitroGateway.Persistence;

/// <summary>
/// 告警规则 EF 实体，映射 alarm_rules 表（snake_case 列名与 M005 迁移一致）。
/// 枚举以字符串存储（ToString），与既有数据格式保持一致。
/// </summary>
public sealed class AlarmRuleEntity
{
    /// <summary>规则 ID（Guid 字符串）</summary>
    public required string Id { get; set; }

    /// <summary>设备 ID（Guid 字符串）</summary>
    public required string DeviceId { get; set; }

    /// <summary>点位 ID（Guid 字符串）</summary>
    public required string PointId { get; set; }

    /// <summary>比较运算符</summary>
    public required string Operator { get; set; }

    /// <summary>阈值（Between 时为下限）</summary>
    public double Threshold { get; set; }

    /// <summary>Between 上限</summary>
    public double? ThresholdUpper { get; set; }

    /// <summary>持续时间（秒）</summary>
    public int DurationSeconds { get; set; }

    /// <summary>严重等级（AlarmSeverity 字符串）</summary>
    public required string Severity { get; set; }

    /// <summary>消息模板</summary>
    public string? MessageTemplate { get; set; }

    /// <summary>是否启用</summary>
    public bool Enabled { get; set; } = true;
}
