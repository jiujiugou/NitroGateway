using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NitroGateway.DeviceManagement.Events;
using NitroGateway.DeviceManagement.Listeners;

namespace NitroGateway.DeviceManagement;

public static class DeviceServiceCollectionExtensions
{
    public static IServiceCollection AddNitroDevice(
        this IServiceCollection services,
        int healthFailureThreshold = 3,
        int healthRecoveryThreshold = 3)
    {
        services.AddScoped<IDeviceManager, DeviceManager>();
        services.AddScoped<IPointManager, PointManager>();
        services.AddSingleton<PointBatchService>();

        // ── HealthMonitor（SST）──
        services.AddSingleton<IDeviceHealthMonitor>(sp =>
        {
            var monitor = new DeviceHealthMonitor(
                sp.GetRequiredService<ILogger<DeviceHealthMonitor>>(),
                healthFailureThreshold,
                healthRecoveryThreshold);

            return monitor;
        });

        // ── Listener（本模块）──
        services.AddSingleton<PersistenceListener>();
        services.AddHostedService<PersistenceListenerRegistrar>();

        return services;
    }
}
