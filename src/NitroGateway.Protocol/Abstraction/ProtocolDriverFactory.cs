using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Protocols;
using NitroGateway.Protocol.Abstractions;

namespace NitroGateway.Protocols;

/// <summary>
/// 复合协议驱动工厂。
/// 具体协议的构造由组合根（<c>AddNitroProtocol</c>）经 <paramref name="createInner"/> 注入，
/// 本类只负责为每个驱动统一包裹可靠性装饰器，不再持有协议清单/注册表。
/// </summary>
public sealed class ProtocolDriverFactory : IProtocolDriverFactory
{
    private readonly IServiceProvider _services;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Func<IServiceProvider, ProtocolIdentifier, DeviceConnection, ILogger, IProtocolDriver> _createInner;

    /// <param name="services">服务提供者，供驱动解析自身依赖（如 ISerialPortManager）</param>
    /// <param name="createInner">协议 → 具体驱动实例的构造委托，由组合根提供</param>
    public ProtocolDriverFactory(
        IServiceProvider services,
        Func<IServiceProvider, ProtocolIdentifier, DeviceConnection, ILogger, IProtocolDriver> createInner)
    {
        _services = services;
        _loggerFactory = services.GetRequiredService<ILoggerFactory>();
        _createInner = createInner;
    }

    /// <inheritdoc />
    public IProtocolDriver Create(ProtocolIdentifier protocol, DeviceConnection connection)
    {
        var inner = _createInner(_services, protocol, connection, _loggerFactory.CreateLogger(protocol.Name));
        return new ReliableProtocolDriver(
            inner,
            _loggerFactory.CreateLogger<ReliableProtocolDriver>(),
            requestTimeout: TimeSpan.FromMilliseconds(Math.Max(100, connection.RequestTimeoutMs)),
            maxRetryAttempts: connection.RetryCount,
            retryDelay: TimeSpan.FromMilliseconds(Math.Max(1, connection.RetryIntervalMs)));
    }
}
