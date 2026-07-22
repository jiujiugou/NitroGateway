using Microsoft.Extensions.DependencyInjection;
using NitroGateway.Protocols.Modbus;
using NitroGateway.Protocols.S7;

namespace NitroGateway.Protocols;

public static class ProtocolServiceCollectionExtensions
{
    /// <summary>注册协议驱动体系。依次：复合工厂 → 各协议注册</summary>
    public static IServiceCollection AddNitroProtocol(this IServiceCollection services)
    {
        var factory = new ProtocolDriverFactory();
        services.AddSingleton(factory);
        services.AddSingleton<IProtocolDriverFactory>(factory);

        // 各协议模块向复合工厂注册驱动
        ModbusRegistration.Register(factory);
        S7Registration.Register(factory);

        return services;
    }
}

