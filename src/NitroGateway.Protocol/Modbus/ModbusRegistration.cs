using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace NitroGateway.Protocols.Modbus;

/// <summary>向复合工厂注册 Modbus 驱动。由 AddNitroProtocol 调用。</summary>
public static class ModbusRegistration
{
    public static void Register(ProtocolDriverFactory factory)
    {
        factory.Register("Modbus", (sp, conn, logger) =>
            string.Equals(conn.Parameters.GetValueOrDefault("Transport")?.ToString(), "RTU", StringComparison.OrdinalIgnoreCase)
                ? new ModbusRtuDriver(conn, sp.GetRequiredService<ISerialPortManager>(), logger)
                : new ModbusTcpDriver(conn, logger));
    }
}
