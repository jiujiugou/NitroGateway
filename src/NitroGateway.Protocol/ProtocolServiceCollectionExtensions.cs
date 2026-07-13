namespace NitroGateway.Protocol;

using Microsoft.Extensions.DependencyInjection;
using NitroGateway.Protocol;
using NitroGateway.Protocol.Modbus;
using NitroGateway.Protocol.S7;
public static class ProtocolServiceCollectionExtensions
{
    /// <summary>注册复合协议驱动工厂（必须在 AddNitroModbus / AddNitroS7 之前调用）</summary>
    public static IServiceCollection AddNitroProtocol(this IServiceCollection services)
    {
        services.AddNitroProtocolFactory();
        services.AddNitroModbus();
        services.AddNitroS7();
        return services;
    }
}