using Microsoft.Extensions.DependencyInjection;

namespace NitroGateway.Command;

/// <summary>命令回写模块 DI 注册扩展（ADR-069）</summary>
public static class CommandServiceCollectionExtensions
{
    /// <summary>
    /// 注册命令回写模块：命令处理器（Singleton）与订阅后台服务。
    /// 依赖（IMqttClient / IWriteService / IConfiguration）由宿主其它模块注册（AddNitroMqtt / AddNitroDevice）。
    /// </summary>
    public static IServiceCollection AddNitroCommand(this IServiceCollection services)
    {
        services.AddSingleton<CommandProcessor>();
        services.AddHostedService<CommandHostedService>();
        return services;
    }
}
