using Microsoft.Extensions.DependencyInjection;

namespace NitroGateway.Protocols.Modbus;

/// <summary>Modbus DI 注册</summary>
public static class ModbusCollectionExtensions
{
    /// <summary>注册 Modbus 协议驱动</summary>
    public static IServiceCollection AddNitroModbus(this IServiceCollection services)
    {
        services.AddSingleton<ModbusAddressParser>();
        services.AddSingleton(sp =>
        {
            sp.GetRequiredService<ProtocolDriverFactory>().Register("Modbus", (conn, logger) =>
            {
                var dialect = conn.Parameters.GetValueOrDefault("Dialect")?.ToString() ?? "TCP";
                return dialect.Equals("RTU", StringComparison.OrdinalIgnoreCase)
                    ? new ModbusRtuDriver(conn, logger)
                    : new ModbusTcpDriver(conn, logger);
            });
            return new object();
        });
        return services;
    }
}
