using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Protocols;

namespace NitroGateway.Protocol.Modbus;

/// <summary>Modbus DI 注册</summary>
public static class ModbusServiceCollectionExtensions
{
    public static IServiceCollection AddNitroModbus(this IServiceCollection services)
    {
        services.AddSingleton<ModbusAddressParser>();
        services.AddSingleton<IProtocolDriverFactory, ModbusDriverFactory>();
        return services;
    }
}

/// <summary>Modbus 驱动工厂。根据 ProtocolIdentifier 创建 TCP 或 RTU 驱动</summary>
public sealed class ModbusDriverFactory : IProtocolDriverFactory
{
    public IProtocolDriver Create(ProtocolIdentifier protocol, DeviceConnection connection)
    {
        if (protocol.Name.Equals("Modbus", StringComparison.OrdinalIgnoreCase))
        {
            var dialect = protocol.Dialect ?? "TCP";
            if (dialect.Equals("RTU", StringComparison.OrdinalIgnoreCase))
                return new ModbusRtuDriver(connection, NullLogger<ModbusRtuDriver>.Instance);
            return new ModbusTcpDriver(connection, NullLogger<ModbusTcpDriver>.Instance);
        }
        throw new NotSupportedException($"不支持的协议: {protocol.Name}");
    }
}
