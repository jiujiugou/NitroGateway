namespace NitroGateway.Collection;

/// <summary>
/// 设备采集熔断器接口。状态机：Closed →（Trip/Offline 信号）→ Open →（冷却到期）→ HalfOpen → Closed/Open。
/// 只负责"保护执行"（放行/拒绝探测），设备健康判定由 HealthMonitor 负责。
/// <para><b>CQS 约定：</b><see cref="State"/> 是纯查询（诊断可读）；<see cref="TryEnterProbe"/>
/// 是唯一带副作用的入口（推进状态、占用探测名额），只允许采集执行路径调用。</para>
/// </summary>
public interface ICircuitBreaker
{
    /// <summary>
    /// 尝试进入一次采集探测（命令，有副作用）。
    /// <para><b>返回值：</b>true = 本次放行执行；false = 本次拒绝执行。</para>
    /// <list type="bullet">
    /// <item>Closed：恒返回 true，正常采集，不占用探测名额；</item>
    /// <item>Open：返回 false（冷却未到）；冷却到期后本方法会先把状态推进到 HalfOpen 再判定；</item>
    /// <item>HalfOpen：仅第一个调用者返回 true 并占用唯一探测名额，其余调用者返回 false；</item>
    /// </list>
    /// <para><b>调用约定：</b>每次采集前调用一次。返回 true 并实际执行后，必须调用
    /// <see cref="RecordSuccess"/> 或 <see cref="RecordFailure"/> 关闭探测；
    /// 否则探测名额会被占用直到实现内部超时（默认 30s）自动释放。</para>
    /// <para><b>副作用：</b>可能触发 Open → HalfOpen 状态推进、占用或释放探测名额。
    /// 只允许在采集执行路径调用；诊断/只读路径请读 <see cref="State"/>（纯查询）。</para>
    /// </summary>
    bool TryEnterProbe();

    /// <summary>当前熔断状态（诊断用，纯查询、无副作用，不推进状态不占探测名额）</summary>
    CircuitState State { get; }

    /// <summary>上报一次成功采集，用于闭合判定</summary>
    void RecordSuccess();

    /// <summary>上报一次失败采集，用于断开判定</summary>
    void RecordFailure();

    /// <summary>强制打开熔断器（由 HealthMonitor Offline 信号触发）</summary>
    void Trip();

    /// <summary>强制重置为闭合状态（由 HealthMonitor Online 信号触发或手动干预）</summary>
    void Reset();
}
