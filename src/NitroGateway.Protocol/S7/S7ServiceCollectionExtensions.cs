using Microsoft.Extensions.DependencyInjection;

namespace NitroGateway.Protocols.S7;

/// <summary>S7 DI 注册</summary>
public static class S7ServiceCollectionExtensions
{
    /// <summary>注册 S7 协议驱动</summary>
    public static IServiceCollection AddNitroS7(this IServiceCollection services)
    {
        services.AddSingleton(sp =>
        {
            sp.GetRequiredService<ProtocolDriverFactory>().Register("S7",
                (conn, logger) => new S7Driver(conn, logger));
            return new object();
        });
        return services;
    }
}
