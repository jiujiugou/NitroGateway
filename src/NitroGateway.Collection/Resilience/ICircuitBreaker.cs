namespace NitroGateway.Collection;

/// <summary>设备采集熔断器接口。Closed →（连续失败）→ Open →（冷却到期）→ HalfOpen → Closed/Open</summary>
public interface ICircuitBreaker
{
    /// <summary>当前是否处于断开状态（拒绝通行）</summary>
    bool IsOpen { get; }

    /// <summary>当前熔断状态（诊断用）</summary>
    CircuitState State { get; }

    /// <summary>上报一次成功采集，用于闭合判定</summary>
    void RecordSuccess();

    /// <summary>上报一次失败采集，用于断开判定</summary>
    void RecordFailure();

    /// <summary>强制重置为闭合状态（外部干预：设备恢复 Online、手动重置）</summary>
    void Reset();
}
