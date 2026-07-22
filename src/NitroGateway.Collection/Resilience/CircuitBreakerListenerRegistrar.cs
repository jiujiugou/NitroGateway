using Microsoft.Extensions.Hosting;
using NitroGateway.DeviceManagement;

namespace NitroGateway.Collection;

/// <summary>启动时将 CircuitBreakerHealthListener 注册到 HealthMonitor</summary>
public sealed class CircuitBreakerListenerRegistrar : IHostedService
{
    public CircuitBreakerListenerRegistrar(
        IDeviceHealthMonitor monitor,
        CircuitBreakerHealthListener listener)
    {
        monitor.AddListener(listener);
    }

    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
