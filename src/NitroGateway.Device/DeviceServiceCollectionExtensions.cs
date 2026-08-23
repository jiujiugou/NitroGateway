using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NitroGateway.DeviceManagement.Events;
using NitroGateway.DeviceManagement.Listeners;
using NitroGateway.Security.Guard;

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

        // ── 写门控（docs/14 §3.2）：Web 由 AddNitroSecurity 注册，桌面不启用 Security 扩展需在此补齐；
        // 两者注册同类型单例，重复注册无害（后注册覆盖，构造等价）。
        services.AddSingleton<RangeValidator>();
        services.AddSingleton<RateLimitValidator>();
        services.AddSingleton<ModeValidator>();
        services.AddSingleton<WriteGuard>();

        // ── 写服务（docs/14 §3.2）：Web 写端点 + 桌面写值共用同一写链路。
        // 依赖全为 Singleton（目录缓存/健康监控/时序存储/驱动池/门控），故服务本身注册为 Singleton。
        services.AddSingleton<IWriteService, WriteService>();

        // ── Listener ──
        services.AddSingleton<IDeviceHealthListener, PersistenceListener>();
        services.AddHostedService<HealthListenerRegistrar>();

        return services;
    }
}
