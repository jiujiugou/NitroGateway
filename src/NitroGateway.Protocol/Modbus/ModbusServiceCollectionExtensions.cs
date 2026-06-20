using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Protocols;

namespace NitroGateway.Protocol.Modbus;

/// <summary>Modbus 驱动 DI 注册</summary>
public static class ModbusServiceCollectionExtensions
{
    /// <summary>注册 Modbus 服务：地址解析器 + 驱动工厂支持</summary>
    public static IServiceCollection AddNitroModbus(this IServiceCollection services)
    {
        services.AddSingleton<ModbusAddressParser>();
        services.AddTransient<IProtocolDriver>(sp =>
        {
            // 具体连接参数在 Create 时传入，此处注册工厂回调
            throw new InvalidOperationException("请通过 IProtocolDriverFactory 创建 ModbusTcpDriver");
        });
        return services;
    }

    /// <summary>根据连接参数创建 Modbus 驱动</summary>
    public static IProtocolDriver CreateModbusTcpDriver(
        DeviceConnection connection, ILogger<ModbusTcpDriver> logger)
    {
        return new ModbusTcpDriver(connection, logger);
    }
}
