using Microsoft.Extensions.DependencyInjection;
using NitroGateway.Scheduler;

namespace NitroGateway.Collection;

/// <summary>Collection 模块 DI 注册</summary>
public static class CollectionServiceCollectionExtensions
{
    /// <summary>注册采集引擎及子模块，并注册到调度器</summary>
    public static IServiceCollection AddNitroCollection(
        this IServiceCollection services, int intervalMs)
    {
        services.AddSingleton<IDeviceReader, DeviceReader>();
        services.AddSingleton<IPointValuePipeline, PointValuePipeline>();
        services.AddSingleton<IDataDispatcher, DataDispatcher>();
        services.AddSingleton<IHealthReporter, HealthReporter>();

        // CollectionEngine 依赖 Scoped 的 IDeviceManager → 也是 Scoped
        services.AddScoped<CollectionEngine>();

        // Scheduler 每次执行时通过 IServiceScopeFactory 创建 scope
        services.AddSingleton(sp =>
        {
            var scheduler = sp.GetRequiredService<IScheduler>();
            var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
            scheduler.Register("CollectAllOnline", intervalMs, async ct =>
            {
                using var scope = scopeFactory.CreateScope();
                var engine = scope.ServiceProvider.GetRequiredService<CollectionEngine>();
                await engine.CollectAllOnlineAsync(ct);
            });
            return scheduler;
        });

        return services;
    }
}
