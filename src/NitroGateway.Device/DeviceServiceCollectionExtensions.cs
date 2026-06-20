using Microsoft.Extensions.DependencyInjection;

namespace NitroGateway.DeviceManagement;

/// <summary>Device 模块 DI 注册</summary>
public static class DeviceServiceCollectionExtensions
{
    /// <summary>注册设备管理服务</summary>
    public static IServiceCollection AddNitroDevice(this IServiceCollection services)
    {
        // 跟随 Storage 的生命周期：DbContext 是 Scoped，消费方也必须是 Scoped
        services.AddScoped<IDeviceManager, DeviceManager>();
        services.AddScoped<IPointManager, PointManager>();
        services.AddSingleton<IDeviceHealthMonitor, DeviceHealthMonitor>();

        // HealthMonitor 触发 → 委托 DeviceManager 更新状态（用 IServiceScopeFactory 创建 scope）
        services.AddSingleton(sp =>
        {
            var monitor = (DeviceHealthMonitor)sp.GetRequiredService<IDeviceHealthMonitor>();
            var factory = sp.GetRequiredService<IServiceScopeFactory>();
            monitor.ThresholdReached += async (deviceId, status) =>
            {
                using var scope = factory.CreateScope();
                var manager = scope.ServiceProvider.GetRequiredService<IDeviceManager>();
                await manager.UpdateStatusAsync(deviceId, status);
            };
            return monitor;
        });

        return services;
    }
}
