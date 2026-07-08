namespace NitroGateway.Alarm.Domain;

/// <summary>
/// 告警规则定义。一台设备的一个点位可以有多个不同严重等级的规则。
/// 工业场景示例：Temperature >50 Info, >70 Warning, >90 Critical, >110 Emergency
/// </summary>
public sealed class AlarmRule
{
    /// <summary>规则唯一标识</summary>
    public Guid Id { get; init; }

    /// <summary>所属设备 ID</summary>
    public required Guid DeviceId { get; init; }

    /// <summary>所属点位 ID</summary>
    public required Guid PointId { get; init; }

    /// <summary>比较运算符：&gt; &lt; &gt;= &lt;= == != Between</summary>
    public required string Operator { get; init; }

    /// <summary>阈值。Between 模式时表示下限</summary>
    public double Threshold { get; init; }

    /// <summary>Between 模式的上限阈值</summary>
    public double? ThresholdUpper { get; init; }

    /// <summary>持续时间（秒）。值超限持续超过此时间才触发告警。0 表示立即触发</summary>
    public int DurationSeconds { get; init; }

    /// <summary>告警严重等级</summary>
    public AlarmSeverity Severity { get; init; }

    /// <summary>告警消息模板。{value} 替换为实际值，{threshold} 替换为阈值</summary>
    public string? MessageTemplate { get; init; }

    /// <summary>是否启用</summary>
    public bool Enabled { get; init; } = true;
}
