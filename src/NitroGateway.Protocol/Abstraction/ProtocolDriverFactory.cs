using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Protocols;
using NitroGateway.Protocol.Abstractions;

namespace NitroGateway.Protocols;

/// <summary>
/// 复合协议驱动工厂。每个协议模块通过 Register 注册自己的驱动构造函数，
/// 最终由 DI 统一注册为一个 IProtocolDriverFactory Singleton。
/// </summary>
public sealed class ProtocolDriverFactory : IProtocolDriverFactory
{
    private readonly Dictionary<string, Func<IServiceProvider, DeviceConnection, ILogger, IProtocolDriver>>
        _factories = new(StringComparer.OrdinalIgnoreCase);
    private readonly IServiceProvider _services;
    private readonly ILoggerFactory _loggerFactory;

    /// <param name="services">服务提供者，供驱动解析自身依赖（如 ISerialPortManager）</param>
    public ProtocolDriverFactory(IServiceProvider services)
    {
        _services = services;
        _loggerFactory = services.GetRequiredService<ILoggerFactory>();
    }

    /// <summary>注册一个协议驱动的构造器</summary>
    /// <param name="protocolName">协议名称（匹配 ProtocolIdentifier.Name），如 "Modbus", "S7"</param>
    /// <param name="factory">接收 ServiceProvider + Connection + Logger，返回 IProtocolDriver 实例</param>
    public void Register(string protocolName, Func<IServiceProvider, DeviceConnection, ILogger, IProtocolDriver> factory)
    {
        _factories[protocolName] = factory;
    }

    /// <inheritdoc />
    public IProtocolDriver Create(ProtocolIdentifier protocol, DeviceConnection connection)
    {
        if (_factories.TryGetValue(protocol.Name, out var factory))
        {
            var inner = factory(_services, connection, _loggerFactory.CreateLogger(protocol.Name));
            // ADR-019 P2-4：重试管线超时与设备单次请求超时对齐（取 RequestTimeoutMs），
            // 避免管线 3s 乐观超时先于设备 5s 超时触发导致误报"超时"与重试窗口被拉长
            // ADR-030 P1：重试次数/首次延迟取 DeviceConnection.RetryCount/RetryIntervalMs（此前硬编码 3 次/500ms，配置不生效）；
            // RetryCount=0 时跳过重试（单次尝试），RetryIntervalMs<=0 时钳到 1ms（Polly 要求正延迟）
            return new ReliableProtocolDriver(
                inner,
                _loggerFactory.CreateLogger<ReliableProtocolDriver>(),
                requestTimeout: TimeSpan.FromMilliseconds(Math.Max(100, connection.RequestTimeoutMs)),
                maxRetryAttempts: connection.RetryCount,
                retryDelay: TimeSpan.FromMilliseconds(Math.Max(1, connection.RetryIntervalMs)));
        }

        throw new NotSupportedException($"不支持的协议: {protocol.Name}");
    }
}
