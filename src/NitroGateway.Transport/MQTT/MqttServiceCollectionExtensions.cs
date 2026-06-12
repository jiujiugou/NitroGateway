using Microsoft.Extensions.DependencyInjection;

namespace NitroGateway.Transport.MQTT;

/// <summary>MQTT 客户端 DI 注册扩展</summary>
public static class MqttServiceCollectionExtensions
{
    /// <summary>
    /// 注册 MQTT 客户端服务。
    /// <see cref="IMqttClient"/> 为 Singleton，整个网关共用一个 MQTT 连接。
    /// </summary>
    /// <param name="services">服务集合</param>
    /// <param name="options">MQTT 连接参数</param>
    public static IServiceCollection AddNitroMqtt(this IServiceCollection services, MqttConnectionOptions options)
    {
        services.AddSingleton(options);
        services.AddSingleton<IMqttClient, MqttClientWrapper>();
        return services;
    }
}
