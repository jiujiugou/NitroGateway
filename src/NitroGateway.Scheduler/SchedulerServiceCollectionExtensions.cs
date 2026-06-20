using Microsoft.Extensions.DependencyInjection;

namespace NitroGateway.Scheduler;

/// <summary>Scheduler 模块 DI 注册</summary>
public static class SchedulerServiceCollectionExtensions
{
    /// <summary>注册调度器（Singleton）</summary>
    public static IServiceCollection AddNitroScheduler(this IServiceCollection services)
    {
        services.AddSingleton<IScheduler, SchedulerEngine>();
        return services;
    }
}
