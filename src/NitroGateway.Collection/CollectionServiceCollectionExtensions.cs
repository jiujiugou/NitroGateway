using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NitroGateway.DeviceManagement;
using NitroGateway.DeviceManagement.Events;
using NitroGateway.Storage.Buffer;

namespace NitroGateway.Collection;

/// <summary>
/// Collection 模块的 DI 注册入口。调用方（Webapi/Host）启动时调用 <see cref="AddNitroCollection"/>
/// 一次性注册采集引擎、读取器、转换管道、数据分发、健康上报、熔断器及两个后台服务
/// （<see cref="MeasurementWriteHost"/>、<see cref="SinkDispatcher"/>）。
/// <para><b>生命周期约定：</b>无状态共享组件（Reader、Pipeline、Dispatcher、熔断器注册表）注册为
/// Singleton；<see cref="DeviceCollector"/> 注册为 Scoped——每轮采集由 <see cref="CollectionEngine"/>
/// 创建独立 scope，避免跨轮共享采集器内部状态。</para>
/// </summary>
public static class CollectionServiceCollectionExtensions
{
    /// <summary>注册采集模块全部服务；从 <paramref name="configuration"/> 的 Collection 节点读取 <see cref="CollectionOption"/> 并注册到 DI。</summary>
    /// <param name="services">DI 容器</param>
    /// <param name="configuration">应用配置；"Collection" 节点缺失时所有项使用 <see cref="CollectionOption"/> 默认值</param>
    /// <returns>同一 <paramref name="services"/>，支持链式调用</returns>
    public static IServiceCollection AddNitroCollection(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // ADR-014：把整个 CollectionOption 绑定进 DI（此前只有 IntervalMs 生效），
        // 让 MaxConcurrency / CircuitBreakerOpenSeconds / CircuitBreakerMaxOpenSeconds 真正生效。
        // ADR-016 P2-2：走标准 Options 管线（Bind + Validate + ValidateOnStart），
        // 非法配置（IntervalMs<=0 / MaxConcurrency<=0 等）启动即报错，不再手动 OptionsWrapper。
        services.AddOptions<CollectionOption>()
            .Bind(configuration.GetSection(CollectionOption.SectionName))
            .Validate(o => o.IntervalMs > 0, "Collection:IntervalMs 必须大于 0（PeriodicTimer 要求正数）")
            .Validate(o => o.MaxConcurrency > 0, "Collection:MaxConcurrency 必须大于 0（否则 SemaphoreSlim 全挂起）")
            .Validate(o => o.CircuitBreakerOpenSeconds >= 0, "Collection:CircuitBreakerOpenSeconds 不能为负")
            .Validate(o => o.CircuitBreakerMaxOpenSeconds >= o.CircuitBreakerOpenSeconds,
                "Collection:CircuitBreakerMaxOpenSeconds 不能小于 CircuitBreakerOpenSeconds")
            .ValidateOnStart();

        // ADR-014：熔断器冷却时长来自配置，不再硬编码 5s / 5min。
        services.AddSingleton<ICircuitBreakerRegistry>(sp =>
        {
            var opt = sp.GetRequiredService<IOptions<CollectionOption>>().Value;
            return new CircuitBreakerRegistry(
                TimeSpan.FromSeconds(opt.CircuitBreakerOpenSeconds),
                TimeSpan.FromSeconds(opt.CircuitBreakerMaxOpenSeconds));
        });

        services.AddSingleton<IDeviceReader, DeviceReader>();
        services.AddSingleton<IPointValuePipeline, PointValuePipeline>();
        // ADR-012：磁盘状态可选注入——未注册 AddNitroSqlite（无 DiskGuardService）的宿主不受影响
        // ADR-011 P3：入队路由与 Forwarder 同一配置（Forwarder:Channels），非法值启动即报错
        var forwardChannels = ResolveForwardChannels(configuration["Forwarder:Channels"] ?? "mqtt");
        services.AddSingleton<IDataDispatcher>(sp => new DataDispatcher(
            sp.GetRequiredService<MeasurementWriteHost>(),
            sp.GetRequiredService<IForwardBuffer>(),
            sp.GetRequiredService<SinkDispatcher>(),
            sp.GetRequiredService<ILogger<DataDispatcher>>(),
            sp.GetService<NitroGateway.Storage.Disk.IDiskStatus>(),
            forwardChannels,
            // ADR-035 第 1 步：站点标识随负载上行（Site:Id，缺省 default）
            NitroGateway.Shared.SiteOptions.Resolve(configuration["Site:Id"])));
        services.AddSingleton<MeasurementWriteHost>();
        services.AddHostedService(sp => sp.GetRequiredService<MeasurementWriteHost>());
        services.AddSingleton<SinkDispatcher>();
        services.AddHostedService(sp => sp.GetRequiredService<SinkDispatcher>());
        services.AddSingleton<IHealthReporter, HealthReporter>();
        // CircuitBreaker 监听 HealthMonitor 的 Online/Offline 信号，驱动熔断器的 Trip/Reset
        services.AddSingleton<IDeviceHealthListener, CircuitBreakerHealthListener>();
        services.AddScoped<IDeviceCollector>(sp => new DeviceCollector(
            sp.GetRequiredService<IDeviceManager>(),
            sp.GetRequiredService<IDeviceReader>(),
            sp.GetRequiredService<IPointValuePipeline>(),
            sp.GetRequiredService<IDataDispatcher>(),
            sp.GetRequiredService<IHealthReporter>(),
            sp.GetRequiredService<ICircuitBreakerRegistry>(),
            sp.GetRequiredService<IDeviceHealthMonitor>(),
            sp.GetRequiredService<ILogger<DeviceCollector>>(),
            // ADR-014：单轮并发上限取自配置，不再使用默认值 5。
            sp.GetRequiredService<IOptions<CollectionOption>>().Value.MaxConcurrency));
        services.AddHostedService<CollectionEngine>();
        return services;
    }

    /// <summary>
    /// 解析北向通道列表（ADR-011 P3）：mqtt / http / both（大小写不敏感）。
    /// 非法值抛 <see cref="ArgumentException"/> 快速失败，与 Forwarder 注册侧校验保持一致。
    /// </summary>
    private static IReadOnlyList<string> ResolveForwardChannels(string channels)
    {
        return channels.Trim().ToLowerInvariant() switch
        {
            "mqtt" => [IForwardBuffer.MqttChannel],
            "http" => [IForwardBuffer.HttpChannel],
            "both" => [IForwardBuffer.MqttChannel, IForwardBuffer.HttpChannel],
            var other => throw new ArgumentException(
                $"Forwarder:Channels 取值必须为 mqtt/http/both，实际为: {other}")
        };
    }
}
