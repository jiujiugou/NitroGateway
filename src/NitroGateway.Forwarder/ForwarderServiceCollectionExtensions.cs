using Microsoft.Extensions.DependencyInjection;
using NitroGateway.Storage.Buffer;

namespace NitroGateway.Forwarder;

/// <summary>Forwarder 模块 DI 注册扩展</summary>
public static class ForwarderServiceCollectionExtensions
{
    /// <summary>
    /// 注册转发模块：自适应节流器、JSON 序列化器、转发器（均为 Singleton）及转发引擎 BackgroundService。
    /// </summary>
    /// <param name="services">DI 容器</param>
    /// <param name="intervalMs">转发引擎轮询间隔（毫秒），如 5000 表示每 5 秒触发一轮；须为正数（PeriodicTimer 要求）</param>
    /// <returns>同一容器，支持链式调用</returns>
    public static IServiceCollection AddNitroForwarder(
        this IServiceCollection services, int intervalMs)
    {
        // ADR-017 P3-2：与 CollectionOption 校验对齐（ADR-016 P2-2）——非法间隔启动即报错并指明字段，
        // 避免 PeriodicTimer 在引擎启动期抛晦涩异常
        if (intervalMs <= 0)
            throw new ArgumentOutOfRangeException(nameof(intervalMs), "转发轮询间隔必须为正数（毫秒）");

        // 节流器用 Singleton：AIMD 状态需跨轮持久，若按作用域注册会在每轮重置为初始值，节流失效
        services.AddSingleton<ForwardingThrottle>();

        services.AddSingleton<IMessageSerializer, JsonMessageSerializer>();
        services.AddSingleton<IForwarder, Forwarder>();

        // ForwarderEngine: BackgroundService + PeriodicTimer，代替原来的 IScheduler 注册
        services.AddHostedService(sp => new ForwarderEngine(
            sp.GetRequiredService<IServiceScopeFactory>(),
            TimeSpan.FromMilliseconds(intervalMs),
            sp.GetRequiredService<IForwardBuffer>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ForwarderEngine>>(),
            sp.GetRequiredService<NitroGateway.Host.GatewayLifecycle>()));

        return services;
    }
}
