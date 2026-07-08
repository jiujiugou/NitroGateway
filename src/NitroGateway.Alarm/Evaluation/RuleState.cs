namespace NitroGateway.Alarm.Evaluation;

/// <summary>单条规则的运行时状态，用于 Duration 计时和去重</summary>
internal sealed class RuleState
{
    /// <summary>上次超限开始时间（用于 Duration 计时）</summary>
    public DateTime? ExceedStartedAt { get; set; }

    /// <summary>当前活跃告警 ID（用于去重：Active 期间不重复生成）</summary>
    public Guid? ActiveAlarmId { get; set; }

    /// <summary>最后采样值</summary>
    public double LastValue { get; set; }
}
