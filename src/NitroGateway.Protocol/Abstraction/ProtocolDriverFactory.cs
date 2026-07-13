using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Protocols;

namespace NitroGateway.Protocols;

/// <summary>
/// 复合协议驱动工厂。每个协议模块通过 Register 注册自己的驱动构造函数，
/// 最终由 DI 统一注册为一个 IProtocolDriverFactory Singleton。
/// </summary>
public sealed class ProtocolDriverFactory : IProtocolDriverFactory
{
    private readonly Dictionary<string, Func<DeviceConnection, ILogger, IProtocolDriver>> _factories = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>注册一个协议驱动的构造器</summary>
    /// <param name="protocolName">协议名称（匹配 ProtocolIdentifier.Name），如 "Modbus", "S7"</param>
    /// <param name="factory">接收 Connection + Logger，返回 IProtocolDriver 实例</param>
    public void Register(string protocolName, Func<DeviceConnection, ILogger, IProtocolDriver> factory)
    {
        _factories[protocolName] = factory;
    }

    /// <inheritdoc />
    public IProtocolDriver Create(ProtocolIdentifier protocol, DeviceConnection connection)
    {
        if (_factories.TryGetValue(protocol.Name, out var factory))
            return factory(connection, NullLogger.Instance);

        throw new NotSupportedException($"不支持的协议: {protocol.Name}");
    }
}
