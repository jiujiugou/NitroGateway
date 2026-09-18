using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Protocols;
using NitroGateway.Protocols.Modbus;
using NitroGateway.Protocols.OpcUa;
using NitroGateway.Protocols.S7;

namespace NitroGateway.Protocols;

public static class ProtocolServiceCollectionExtensions
{
    /// <summary>
    /// 注册协议驱动体系。协议清单以编译期 switch 提供（<see cref="CreateInnerDriver"/>），
    /// 新增协议只需改这一处，无需再维护注册表。
    /// </summary>
    public static IServiceCollection AddNitroProtocol(this IServiceCollection services)
    {
        // 单例工厂：协议的构造委托由组合根（本方法）注入，工厂本身不感知具体协议
        services.AddSingleton(sp => new ProtocolDriverFactory(sp, CreateInnerDriver));
        services.AddSingleton<IProtocolDriverFactory>(sp => sp.GetRequiredService<ProtocolDriverFactory>());

        // 长连接驱动池：按设备复用驱动，设备变更时由 DeviceManager 触发 Evict
        services.AddSingleton<IProtocolDriverPool, ProtocolDriverPool>();
        services.AddSingleton<ISerialPortManager, SerialPortManager>();
        return services;
    }

    /// <summary>
    /// 协议名 → 具体驱动实例。协议名匹配 <see cref="ProtocolIdentifier.Name"/>（忽略大小写）。
    /// Modbus 按连接参数 Transport 区分 TCP/RTU。
    /// </summary>
    private static IProtocolDriver CreateInnerDriver(
        IServiceProvider sp,
        ProtocolIdentifier protocol,
        DeviceConnection connection,
        ILogger logger)
        => protocol.Name.ToUpperInvariant() switch
        {
            "MODBUS" when string.Equals(
                    connection.Parameters.GetValueOrDefault("Transport")?.ToString(),
                    "RTU", StringComparison.OrdinalIgnoreCase)
                => new ModbusRtuDriver(connection, sp.GetRequiredService<ISerialPortManager>(), logger),
            "MODBUS" => new ModbusTcpDriver(connection, logger),
            "S7" => new S7Driver(connection, logger),
            "OPC UA" => new OpcUaDriver(connection, logger),
            _ => throw new NotSupportedException($"不支持的协议: {protocol.Name}")
        };
}
