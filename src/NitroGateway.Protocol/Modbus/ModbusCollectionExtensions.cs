using Microsoft.Extensions.DependencyInjection;

namespace NitroGateway.Protocols.Modbus;

/// <summary>Modbus DI 注册</summary>
public static class ModbusCollectionExtensions
{
    /// <summary>注册 Modbus 协议驱动到复合工厂</summary>
    public static IServiceCollection AddNitroModbus(this IServiceCollection services)
    {
        services.AddSingleton<ModbusAddressParser>();
        return services;
    }
}

/// <summary>向复合工厂注册 Modbus 驱动。由 AddNitroProtocol 调用</summary>
public static class ModbusRegistration
{
    public static void Register(ProtocolDriverFactory factory)
    {
        factory.Register("Modbus", (conn, logger) =>
        {
            var dialect = conn.Parameters.GetValueOrDefault("Dialect")?.ToString() ?? "TCP";
            return dialect.Equals("RTU", StringComparison.OrdinalIgnoreCase)
                ? new ModbusRtuDriver(conn, logger)
                : new ModbusTcpDriver(conn, logger);
        });
    }
}
