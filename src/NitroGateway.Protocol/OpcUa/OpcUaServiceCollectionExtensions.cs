using Microsoft.Extensions.DependencyInjection;

namespace NitroGateway.Protocols.OpcUa;

/// <summary>OPC UA 模块 DI 注册</summary>
public static class OpcUaServiceCollectionExtensions
{
    /// <summary>注册 OPC UA 地址解析器</summary>
    public static IServiceCollection AddNitroOpcUa(this IServiceCollection services)
    {
        services.AddSingleton<OpcUaAddressParser>();
        return services;
    }
}
