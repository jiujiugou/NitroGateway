using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NitroGateway.Protocols.Modbus;
using NitroGateway.Protocols.OpcUa;
using NitroGateway.Protocols.S7;

namespace NitroGateway.Protocols;

public static class ProtocolServiceCollectionExtensions
{
    /// <summary>注册协议驱动体系。依次：复合工厂 → 各协议注册</summary>
    public static IServiceCollection AddNitroProtocol(this IServiceCollection services)
    {
        services.AddNitroModbus();

        // 单例工厂：首次解析时创建实例并注册所有协议模块
        services.AddSingleton(sp =>
        {
            var factory = new ProtocolDriverFactory(sp);
            ModbusRegistration.Register(factory);
            OpcUaRegistration.Register(factory);
            S7Registration.Register(factory);
            return factory;
        });
        services.AddSingleton<IProtocolDriverFactory>(sp => sp.GetRequiredService<ProtocolDriverFactory>());

        // 长连接驱动池：按设备复用驱动，设备变更时由 DeviceManager 触发 Evict
        services.AddSingleton<IProtocolDriverPool, ProtocolDriverPool>();

        return services;
    }
}
