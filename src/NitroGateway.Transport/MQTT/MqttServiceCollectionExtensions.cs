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

        // 自动生成唯一 ClientId
        if (string.IsNullOrWhiteSpace(options.ClientId))
        {
            options = options with { ClientId = $"NitroGateway-{Environment.MachineName}-{Guid.NewGuid():N}"[..8] };
        }

        services.AddSingleton(options);
        services.AddSingleton<IMqttClient, MqttClientWrapper>();
        services.AddHostedService<MqttHostedService>();
        return services;
    }
}
