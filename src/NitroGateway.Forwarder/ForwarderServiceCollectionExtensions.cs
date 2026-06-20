using Microsoft.Extensions.DependencyInjection;
using NitroGateway.Scheduler;

namespace NitroGateway.Forwarder;

/// <summary>Forwarder 模块 DI 注册</summary>
public static class ForwarderServiceCollectionExtensions
{
    /// <summary>注册转发器，并注册到调度器</summary>
    public static IServiceCollection AddNitroForwarder(
        this IServiceCollection services, int intervalMs)
    {
        services.AddSingleton<IMessageSerializer, JsonMessageSerializer>();
        services.AddSingleton<IForwarder, Forwarder>();

        // Scheduler
        services.AddSingleton(sp =>
        {
            var scheduler = sp.GetRequiredService<IScheduler>();
            var forwarder = sp.GetRequiredService<IForwarder>();
            scheduler.Register("Forward", intervalMs,
                ct => forwarder.ForwardBatchAsync(int.MaxValue, ct));
            return scheduler;
        });

        return services;
    }
}
