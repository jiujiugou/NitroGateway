using Microsoft.Extensions.DependencyInjection;

namespace NitroGateway.Protocols.S7;

/// <summary>向复合工厂注册 S7 驱动。由 AddNitroProtocol 调用</summary>
public static class S7Registration
{
    public static void Register(ProtocolDriverFactory factory)
    {
        factory.Register("S7", (_, conn, logger) => new S7Driver(conn, logger));
    }
}
