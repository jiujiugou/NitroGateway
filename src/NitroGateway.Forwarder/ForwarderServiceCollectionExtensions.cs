using Microsoft.Extensions.DependencyInjection;

namespace NitroGateway.Forwarder;

/// <summary>Forwarder 模块 DI 注册</summary>
public static class ForwarderServiceCollectionExtensions
{
    /// <summary>注册转发器、自适应节流器及转发引擎 BackgroundService</summary>
    public static IServiceCollection AddNitroForwarder(
        this IServiceCollection services, int intervalMs)
    {
        // 自适应节流器（Singleton：跨调度周期保持状态）
        services.AddSingleton<ForwardingThrottle>();

        services.AddSingleton<IMessageSerializer, JsonMessageSerializer>();
        services.AddSingleton<IForwarder, Forwarder>();

        // ForwarderEngine: BackgroundService + PeriodicTimer，代替原来的 IScheduler 注册
        services.AddHostedService(sp => new ForwarderEngine(
            sp.GetRequiredService<IServiceScopeFactory>(),
            TimeSpan.FromMilliseconds(intervalMs),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ForwarderEngine>>()));

        return services;
    }
}
