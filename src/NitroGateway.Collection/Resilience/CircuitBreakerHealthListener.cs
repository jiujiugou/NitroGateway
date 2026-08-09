using NitroGateway.DeviceManagement.Events;
using NitroGateway.Domain.Devices;

namespace NitroGateway.Collection;

/// <summary>
/// 熔断器健康监听器：接收 HealthMonitor 的信号，触发 CircuitBreaker 的保护/恢复。
/// <list type="bullet">
/// <item>设备 Online → Reset()：恢复闭合，重置冷却</item>
/// <item>设备 Offline → Trip()：强制打开，防止雪崩</item>
/// </list>
/// </summary>
public sealed class CircuitBreakerHealthListener : IDeviceHealthListener
{
    private readonly ICircuitBreakerRegistry _breakers;

    /// <summary>创建健康监听器。</summary>
    /// <param name="breakers">熔断器注册表</param>
    public CircuitBreakerHealthListener(ICircuitBreakerRegistry breakers)
    {
        _breakers = breakers;
    }

    /// <summary>
    /// 处理健康状态变更：Online → 重置熔断器（恢复闭合）；Offline → 打开熔断器（防雪崩）。
    /// Unknown/Error/Maintenance 不处理（Error 仍允许采集探测）。
    /// </summary>
    /// <param name="e">健康状态变更事件</param>
    /// <param name="ct">取消令牌（本实现同步完成，不使用）</param>
    public ValueTask OnHealthChangedAsync(DeviceHealthChanged e, CancellationToken ct = default)
    {
        switch (e.NewStatus)
        {
            case DeviceStatus.Online:
                _breakers.Reset(e.DeviceId);
                break;
            case DeviceStatus.Offline:
                _breakers.Get(e.DeviceId).Trip();
                break;
        }
        return ValueTask.CompletedTask;
    }
}
