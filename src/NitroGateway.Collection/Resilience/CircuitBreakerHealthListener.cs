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

    public CircuitBreakerHealthListener(ICircuitBreakerRegistry breakers)
    {
        _breakers = breakers;
    }

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
