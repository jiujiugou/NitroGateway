using Microsoft.Extensions.DependencyInjection;

namespace NitroGateway.Protocols.S7;

/// <summary>S7 DI 注册</summary>
public static class S7ServiceCollectionExtensions
{
    /// <summary>注册 S7 协议驱动到复合工厂</summary>
    public static IServiceCollection AddNitroS7(this IServiceCollection services)
    {
        return services;
    }
}

/// <summary>向复合工厂注册 S7 驱动。由 AddNitroProtocol 调用</summary>
public static class S7Registration
{
    public static void Register(ProtocolDriverFactory factory)
    {
        factory.Register("S7", (conn, logger) => new S7Driver(conn, logger));
    }
}
