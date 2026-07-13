using Microsoft.Extensions.DependencyInjection;
namespace NitroGateway.Protocols;

/// <summary>Protocol 抽象层 DI 注册</summary>
public static class ProtocolFactoryServiceCollectionExtensions
{
    /// <summary>注册复合协议驱动工厂（必须在 AddNitroModbus / AddNitroS7 之前调用）</summary>
    public static IServiceCollection AddNitroProtocolFactory(this IServiceCollection services)
    {
        services.AddSingleton<ProtocolDriverFactory>();
        services.AddSingleton<IProtocolDriverFactory>(sp => sp.GetRequiredService<ProtocolDriverFactory>());
        return services;
    }
}
