namespace NitroGateway.Alarm.Domain;

/// <summary>
/// 告警生命周期状态。
/// Normal → Pending(Duration 计时) → Active → Acknowledged → Resolved
/// </summary>
public enum AlarmState
{
    /// <summary>未触发</summary>
    Normal,

    /// <summary>超限但未满足持续时间，计时中</summary>
    Pending,

    /// <summary>已触发，当前活跃</summary>
    Active,

    /// <summary>操作员已确认</summary>
    Acknowledged,

    /// <summary>已恢复</summary>
    Resolved
}
