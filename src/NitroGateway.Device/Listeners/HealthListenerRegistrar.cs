using Microsoft.Extensions.Hosting;
using NitroGateway.DeviceManagement.Events;

namespace NitroGateway.DeviceManagement.Listeners;

/// <summary>启动时将 DI 中所有 IDeviceHealthListener 注册到 HealthMonitor</summary>
public sealed class HealthListenerRegistrar : IHostedService
{
    public HealthListenerRegistrar(
        IDeviceHealthMonitor monitor,
        IEnumerable<IDeviceHealthListener> listeners)
    {
        foreach (var listener in listeners)
            monitor.AddListener(listener);
    }

    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
