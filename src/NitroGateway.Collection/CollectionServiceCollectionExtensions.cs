using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NitroGateway.Collection.Cache;
using NitroGateway.DeviceManagement;
using NitroGateway.Domain.Events;

namespace NitroGateway.Collection;

/// <summary>Collection 模块 DI 注册</summary>
public static class CollectionServiceCollectionExtensions
{
    /// <summary>
    /// 注册采集引擎及子模块。
    /// </summary>
    /// <param name="intervalMs">采集间隔（毫秒），实际由 <see cref="CollectionEngine"/> 控制</param>
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
        // ── 设备运行时缓存（Singleton，采集线程读内存不查 DB）──
        services.AddSingleton<DeviceCache>();
        services.AddSingleton<IDeviceChangeSink>(sp => sp.GetRequiredService<DeviceCache>());

        // ── 熔断器注册表（Singleton，跨采集轮次保持状态）──
        services.AddSingleton<ICircuitBreakerRegistry>(_ =>
            new CircuitBreakerRegistry(
                failureThreshold: circuitBreakerThreshold,
                openDuration: TimeSpan.FromSeconds(circuitBreakerOpenSeconds),
                maxOpenDuration: TimeSpan.FromMinutes(5)));

        // ── 采集管道各组件（Singleton，无状态或自行管理状态）──
        services.AddSingleton<IDeviceReader, DeviceReader>();
        services.AddSingleton<IPointValuePipeline, PointValuePipeline>();
        services.AddSingleton<IDataDispatcher, DataDispatcher>();
        services.AddSingleton<IHealthReporter, HealthReporter>();

        // ── DeviceCollector（Scoped，与 DeviceManager 一致）──
        services.AddScoped<IDeviceCollector>(sp =>
        {
            return new DeviceCollector(
                sp.GetRequiredService<IDeviceManager>(),
                sp.GetRequiredService<DeviceCache>(),
                sp.GetRequiredService<IDeviceReader>(),
                sp.GetRequiredService<IPointValuePipeline>(),
                sp.GetRequiredService<IDataDispatcher>(),
                sp.GetRequiredService<IHealthReporter>(),
                sp.GetRequiredService<ICircuitBreakerRegistry>(),
                sp.GetRequiredService<ILogger<CollectionEngine>>(),
                maxConcurrency);
        });

        // ── 采集引擎 BackgroundService ──
        services.AddHostedService<CollectionEngine>();

        return services;
    }
}
