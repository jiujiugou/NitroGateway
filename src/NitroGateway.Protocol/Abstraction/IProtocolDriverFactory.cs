using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Protocols;

namespace NitroGateway.Protocol;

/// <summary>根据协议标识和连接参数创建驱动实例</summary>
public interface IProtocolDriverFactory
{
    IProtocolDriver Create(ProtocolIdentifier protocol, DeviceConnection connection);
}
