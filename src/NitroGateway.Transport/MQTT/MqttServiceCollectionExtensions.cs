using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace NitroGateway.Transport.MQTT;

/// <summary>MQTT 客户端 DI 注册扩展</summary>
public static class MqttServiceCollectionExtensions
{
    /// <summary>
    /// 从 IConfiguration 的 "MQTT" 节点读取 <see cref="MqttConnectionOptions"/>，
    /// 自动生成 ClientId（NitroGateway-{MachineName}-{随机后缀}）。
    /// </summary>
    public static IServiceCollection AddNitroMqtt(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection("MQTT").Get<MqttConnectionOptions>();

        // 自动生成唯一 ClientId。
        // ADR-006 P1-1：修复前对整串取 [..8]，所有实例恒为 "NitroGat"，多实例连同一 broker 会按 MQTT 规范互踢；
        // 现在只截 GUID 后缀 8 位，前缀保留 MachineName 便于排查，保证实例间唯一。
        if (string.IsNullOrWhiteSpace(options.ClientId))
        {
            var guidSuffix = Guid.NewGuid().ToString("N")[..8];
            options = options with { ClientId = $"NitroGateway-{Environment.MachineName}-{guidSuffix}" };
        }

        services.AddSingleton(options);
        services.AddSingleton<IMqttClient, MqttClientWrapper>();
        services.AddHostedService<MqttHostedService>();
        return services;
    }
}
