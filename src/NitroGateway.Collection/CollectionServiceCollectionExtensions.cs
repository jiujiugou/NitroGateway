using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NitroGateway.Collection.Resilience;
using NitroGateway.DeviceManagement;

namespace NitroGateway.Collection;

/// <summary>Collection 模块 DI 注册</summary>
public static class CollectionServiceCollectionExtensions
{
    /// <summary>
    /// 注册采集引擎及子模块。
    /// </summary>
    /// <param name="intervalMs">采集间隔（毫秒）</param>
    /// <param name="maxConcurrency">最大并发采集设备数。默认 5</param>
    /// <param name="circuitBreakerThreshold">熔断器连续失败阈值。默认 5</param>
    /// <param name="circuitBreakerOpenSeconds">熔断打开冷却秒数。默认 30</param>
    public static IServiceCollection AddNitroCollection(
        this IServiceCollection services,
        int intervalMs,
        int maxConcurrency = 5,
        int circuitBreakerThreshold = 5,
        int circuitBreakerOpenSeconds = 30)
    {
        // ── 熔断器注册表（Singleton）──
        services.AddSingleton<ICircuitBreakerRegistry>(_ =>
            new CircuitBreakerRegistry(
                failureThreshold: circuitBreakerThreshold,
                openDuration: TimeSpan.FromSeconds(circuitBreakerOpenSeconds),
                maxOpenDuration: TimeSpan.FromMinutes(5)));

        // ── 采集管道 ──
        services.AddSingleton<IDeviceReader, DeviceReader>();
        services.AddSingleton<IPointValuePipeline, PointValuePipeline>();
        services.AddSingleton<IDataDispatcher, DataDispatcher>();
        services.AddSingleton<SinkDispatcher>();
        services.AddSingleton<IHealthReporter, HealthReporter>();
        services.AddHostedService<DeviceCircuitBreakerSyncService>();
        // ── DeviceCollector（Scoped）──
        services.AddScoped<IDeviceCollector>(sp =>
        {
            return new DeviceCollector(
                sp.GetRequiredService<IDeviceManager>(),
                sp.GetRequiredService<IDeviceReader>(),
                sp.GetRequiredService<IPointValuePipeline>(),
                sp.GetRequiredService<IDataDispatcher>(),
                sp.GetRequiredService<IHealthReporter>(),
                sp.GetRequiredService<ICircuitBreakerRegistry>(),
                sp.GetRequiredService<ILogger<CollectionEngine>>(),
                maxConcurrency);
        });

        // ── 设备恢复 → 熔断器联动 ──
        services.AddHostedService<DeviceCircuitBreakerSyncService>();

        // ── 采集引擎 ──
        services.AddHostedService(sp => new CollectionEngine(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<NitroGateway.Host.GatewayLifecycle>(),
            TimeSpan.FromMilliseconds(intervalMs),
            sp.GetRequiredService<ILogger<CollectionEngine>>()));

        return services;
    }
}
