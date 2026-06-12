using Microsoft.Extensions.DependencyInjection;

namespace NitroGateway.Transport.HTTP;

/// <summary>HTTP 客户端 DI 注册扩展</summary>
public static class HttpServiceCollectionExtensions
{
    /// <summary>注册 HTTP 客户端服务。默认 Singleton</summary>
    public static IServiceCollection AddNitroHttp(this IServiceCollection services, HttpConnectionOptions options)
    {
        services.AddSingleton(options);
        services.AddSingleton<IHttpClient, HttpClientWrapper>();
        return services;
    }
}
