using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace NitroGateway.Shared;

/// <summary>
/// 各模块依赖注入的通用扩展方法，提供统一的注册规范和辅助方法。
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// 按接口扫描并注册服务。
    /// 约定：名为 IXxx 的接口自动绑定到同命名空间下的 Xxx 实现类。
    /// </summary>
    /// <param name="services">服务集合</param>
    /// <param name="assemblyMarker">实现类所在程序集中的标记类型</param>
    /// <param name="lifetime">生命周期，默认 Singleton（网关服务大多是长期运行的）</param>
    public static IServiceCollection AddServicesFromAssembly(
        this IServiceCollection services,
        Type assemblyMarker,
        ServiceLifetime lifetime = ServiceLifetime.Singleton)
    {
        var assembly = assemblyMarker.Assembly;
        var types = assembly.GetExportedTypes();

        foreach (var impl in types.Where(t => t is { IsClass: true, IsAbstract: false }))
        {
            var iface = impl.GetInterfaces()
                            .FirstOrDefault(i => i.Name == $"I{impl.Name}");
            if (iface is not null)
            {
                services.Add(new ServiceDescriptor(iface, impl, lifetime));
            }
        }

        return services;
    }

    /// <summary>
    /// 获取工厂方法的推荐日志类别。
    /// 示例：<c>services.GetLogger<DeviceManager>()</c>
    /// </summary>
    public static ILogger<T> GetLogger<T>(this IServiceProvider sp) =>
        sp.GetRequiredService<ILoggerFactory>().CreateLogger<T>();
}
