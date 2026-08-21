using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NitroGateway.Storage.Buffer;

namespace NitroGateway.Transport.MQTT;

/// <summary>MQTT 客户端 DI 注册扩展</summary>
public static class MqttServiceCollectionExtensions
{
    /// <summary>
    /// 从 IConfiguration 的 "MQTT" 节点读取 <see cref="MqttConnectionOptions"/>，
    /// 自动生成 ClientId（NitroGateway-{MachineName}-{随机后缀}）。
    /// ADR-020 P3-2：走标准 Options 管线（Bind + Validate + ValidateOnStart）——配置缺 MQTT 段或
    /// Host 为空、Port 越界时启动即明确报错（修复前缺段直接 NRE、空 Host 不校验）。
    /// </summary>
    public static IServiceCollection AddNitroMqtt(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<MqttConnectionOptions>()
            .Bind(configuration.GetSection(MqttConnectionOptions.SectionName))
            .Validate(o => !string.IsNullOrWhiteSpace(o.Host), "MQTT:Host 不能为空（MQTTnet 需要 broker 地址）")
            .Validate(o => o.Port is >= 1 and <= 65535, "MQTT:Port 必须在 1-65535")
            .ValidateOnStart();

        // 自动生成唯一 ClientId（ADR-006 P1-1）：只截 GUID 后缀 8 位，前缀保留 MachineName 便于排查，保证实例间唯一。
        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<MqttConnectionOptions>>().Value;
            if (string.IsNullOrWhiteSpace(options.ClientId))
            {
                var guidSuffix = Guid.NewGuid().ToString("N")[..8];
                return options with { ClientId = $"NitroGateway-{Environment.MachineName}-{guidSuffix}" };
            }
            return options;
        });

        // ADR-061：连接层注入转发总开关（关闭即断开 + 停止重连）。
        // 用 GetService（null 安全）解析而非构造函数注入——未注册开关的宿主
        // （如 Ingest 中心，无转发 UI）得到 null → 恒启用，行为与旧版一致；
        // MS.DI 不按默认值回退，直接构造函数注入会在 Ingest 启动时抛解析异常。
        services.AddSingleton<IMqttClient>(sp => new MqttClientWrapper(
            sp.GetRequiredService<MqttConnectionOptions>(),
            sp.GetRequiredService<ILogger<MqttClientWrapper>>(),
            sp.GetServices<IMqttStateListener>(),
            sp.GetService<IForwardMqttToggle>()));
        services.AddHostedService<MqttHostedService>();
        return services;
    }
}
