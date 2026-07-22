using Microsoft.Extensions.Hosting;

namespace NitroGateway.DeviceManagement.Listeners;

/// <summary>启动时将 PersistenceListener 注册到 HealthMonitor</summary>
public sealed class PersistenceListenerRegistrar : IHostedService
{
    public PersistenceListenerRegistrar(
        IDeviceHealthMonitor monitor,
        PersistenceListener listener)
    {
        monitor.AddListener(listener);
    }

    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
