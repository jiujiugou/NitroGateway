using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using NitroGateway.Storage.Buffer;
using NitroGateway.Storage.Disk;
using NitroGateway.Transport.HTTP;

namespace NitroGateway.Forwarder;

/// <summary>Forwarder 模块 DI 注册扩展</summary>
public static class ForwarderServiceCollectionExtensions
{
    /// <summary>
    /// 注册转发模块：JSON 序列化器、转发器（均为 Singleton）及转发引擎 BackgroundService。
    /// 旧签名仅注册 MQTT 通道（Channels 默认 mqtt），供独立测试/兼容调用使用；
    /// 生产宿主请使用 <see cref="AddNitroForwarder(IServiceCollection, IConfiguration)"/> 配置驱动注册。
    /// </summary>
    /// <param name="services">DI 容器</param>
    /// <param name="intervalMs">转发引擎轮询间隔（毫秒），如 5000 表示每 5 秒触发一轮；须为正数（PeriodicTimer 要求）</param>
    /// <returns>同一容器，支持链式调用</returns>
    public static IServiceCollection AddNitroForwarder(
        this IServiceCollection services, int intervalMs)
    {
        // 避免 PeriodicTimer 在引擎启动期抛晦涩异常
        if (intervalMs <= 0)
            throw new ArgumentOutOfRangeException(nameof(intervalMs), "转发轮询间隔必须为正数（毫秒）");

        return AddNitroForwarderCore(services, new ForwarderOption { IntervalMs = intervalMs });
    }

    public static IServiceCollection AddNitroForwarder(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ForwarderOption>()
            .Bind(configuration.GetSection(ForwarderOption.SectionName))
            .Validate(o => o.IntervalMs > 0, "Forwarder:IntervalMs 必须大于 0（PeriodicTimer 要求正数）")
            .Validate(o => IsSupportedChannels(o.Channels), "Forwarder:Channels 必须为 mqtt/http/both")
            .Validate(o => !ChannelsContainHttp(o.Channels) || !string.IsNullOrWhiteSpace(o.Http.BaseUrl),
                "启用 http 通道时 Forwarder:Http:BaseUrl 不能为空")
            .ValidateOnStart();

        // 注册期即解析通道决定引擎注册（Options 校验在宿主启动才触发，引擎必须在启动前就绪）
        var option = new ForwarderOption();
        configuration.GetSection(ForwarderOption.SectionName).Bind(option);
        return AddNitroForwarderCore(services, option);
    }

    /// <summary>按配置注册转发引擎与依赖（MQTT/HTTP 通道按 Channels 拆分）</summary>
    private static IServiceCollection AddNitroForwarderCore(
        IServiceCollection services, ForwarderOption option)
    {
        var channels = option.ResolveChannels();

        services.AddSingleton<IMessageSerializer, JsonMessageSerializer>();
        // ADR-035 第 1 步：站点标识注入转发器（Site:Id，缺省 default）
        services.AddSingleton<IForwarder>(sp => new Forwarder(
            sp.GetRequiredService<IForwardBuffer>(),
            sp.GetRequiredService<IMessageSerializer>(),
            sp.GetRequiredService<NitroGateway.Transport.MQTT.IMqttClient>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Forwarder>>(),
            NitroGateway.Shared.SiteOptions.Resolve(
                sp.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>()["Site:Id"])));

        if (channels.Contains(IForwardBuffer.MqttChannel))
        {
            // ForwarderEngine: BackgroundService + PeriodicTimer，代替原来的 IScheduler 注册
            services.AddHostedService(sp => new ForwarderEngine(
                sp.GetRequiredService<IServiceScopeFactory>(),
                TimeSpan.FromMilliseconds(option.IntervalMs),
                sp.GetRequiredService<IForwardBuffer>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ForwarderEngine>>(),
                sp.GetRequiredService<NitroGateway.Host.GatewayLifecycle>(),
                sp.GetService<IDiskStatus>()));
        }

        if (channels.Contains(IForwardBuffer.HttpChannel))
        {
            services.AddNitroHttp(new HttpConnectionOptions
            {
                BaseUrl = option.Http.BaseUrl,
                TimeoutMs = option.Http.TimeoutMs,
                MaxRetries = option.Http.MaxRetries,
                AuthType = option.Http.AuthType,
                BearerToken = option.Http.BearerToken,
                HealthPath = option.Http.HealthPath
            });
            services.AddHostedService(sp => new HttpForwarderEngine(
                sp.GetRequiredService<IServiceScopeFactory>(),
                TimeSpan.FromMilliseconds(option.IntervalMs),
                sp.GetRequiredService<IForwardBuffer>(),
                option.Http.Path ?? "/api/measurements/batch",
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<HttpForwarderEngine>>()));
        }

        return services;
    }

    /// <summary>Channels 取值是否为 mqtt/http/both 之一（大小写不敏感）</summary>
    private static bool IsSupportedChannels(string channels)
    {
        var value = channels.Trim();
        return value.Equals("mqtt", StringComparison.OrdinalIgnoreCase)
            || value.Equals("http", StringComparison.OrdinalIgnoreCase)
            || value.Equals("both", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Channels 是否启用 http 通道（http / both）</summary>
    private static bool ChannelsContainHttp(string channels)
    {
        var value = channels.Trim();
        return value.Equals("http", StringComparison.OrdinalIgnoreCase)
            || value.Equals("both", StringComparison.OrdinalIgnoreCase);
    }
}
