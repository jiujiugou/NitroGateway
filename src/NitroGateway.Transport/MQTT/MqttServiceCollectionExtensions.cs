using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NitroGateway.Storage.Buffer;

namespace NitroGateway.Transport.MQTT;

/// <summary>MQTT 客户端 DI 注册扩展</summary>
public static class MqttServiceCollectionExtensions
{
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
