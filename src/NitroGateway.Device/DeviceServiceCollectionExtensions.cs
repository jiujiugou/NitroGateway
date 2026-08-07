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
        // ADR-002 P2-2：设备目录内存缓存（采集热路径避免每秒全量 EF 映射）
        services.AddSingleton<IDeviceSnapshotCache, DeviceSnapshotCache>();
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

        // ── Listener ──
        services.AddSingleton<IDeviceHealthListener, PersistenceListener>();
        services.AddHostedService<HealthListenerRegistrar>();

        return services;
    }
}
