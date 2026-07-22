using NitroGateway.DeviceManagement.Events;
using NitroGateway.Domain.Devices;

namespace NitroGateway.Collection;

/// <summary>熔断器监听器：设备恢复 Online → 强制闭合 CircuitBreaker</summary>
public sealed class CircuitBreakerHealthListener : IDeviceHealthListener
{
    private readonly ICircuitBreakerRegistry _breakers;

    public CircuitBreakerHealthListener(ICircuitBreakerRegistry breakers) { _breakers = breakers; }

    public ValueTask OnHealthChangedAsync(DeviceHealthChanged e, CancellationToken ct = default)
    {
        if (e.NewStatus == DeviceStatus.Online)
            _breakers.Reset(e.DeviceId);
        return ValueTask.CompletedTask;
    }
}
