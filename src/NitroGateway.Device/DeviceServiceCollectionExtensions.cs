using Microsoft.Extensions.DependencyInjection;

namespace NitroGateway.DeviceManagement;

/// <summary>Device 模块 DI 注册</summary>
public static class DeviceServiceCollectionExtensions
{
    /// <summary>
    /// 注册设备管理服务。
    /// </summary>
    /// <param name="healthFailureThreshold">健康监控连续失败阈值（触发 Offline）。默认 10</param>
    /// <param name="healthRecoveryThreshold">健康监控连续成功阈值（触发 Online 恢复）。默认 3</param>
    public static IServiceCollection AddNitroDevice(
        this IServiceCollection services,
        int healthFailureThreshold = 10,
        int healthRecoveryThreshold = 3)
    {
        // 跟随 Storage 的生命周期：DbContext 是 Scoped，消费方也必须是 Scoped
        services.AddScoped<IDeviceManager, DeviceManager>();
        services.AddScoped<IPointManager, PointManager>();

        // 点位批量服务（Singleton，无状态）
        services.AddSingleton<PointBatchService>();

        // HealthMonitor — Singleton，可配置阈值
        services.AddSingleton<IDeviceHealthMonitor>(sp =>
            new DeviceHealthMonitor(
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<DeviceHealthMonitor>>(),
                healthFailureThreshold,
                healthRecoveryThreshold));

        // HealthMonitor 触发 → 委托 DeviceManager 更新设备状态（用 IServiceScopeFactory 创建 scope）
        services.AddSingleton(sp =>
        {
            var monitor = (DeviceHealthMonitor)sp.GetRequiredService<IDeviceHealthMonitor>();
            var factory = sp.GetRequiredService<IServiceScopeFactory>();
            monitor.StatusChanged += async (deviceId, status) =>
            {
                monitor.UpdateStatus(deviceId, status);
                using var scope = factory.CreateScope();
                var manager = scope.ServiceProvider.GetRequiredService<IDeviceManager>();
                await manager.UpdateStatusAsync(deviceId, status);
            };
            return monitor;
        });

        return services;
    }
}
