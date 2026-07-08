using Microsoft.Extensions.DependencyInjection;

namespace NitroGateway.Host;

public static class HostServiceCollectionExtension
{
    public static IServiceCollection AddNitroGatewayHost(this IServiceCollection services)
    {
        services.AddSingleton<GatewayLifecycle>();
        return services;
    }
}
