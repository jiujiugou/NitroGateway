using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NitroGateway.DeviceManagement;
using NitroGateway.DeviceManagement.Events;

namespace NitroGateway.Collection;

public static class CollectionServiceCollectionExtensions
{
    public static IServiceCollection AddNitroCollection(
        this IServiceCollection services,
        int intervalMs,
        int maxConcurrency = 5,
        int circuitBreakerOpenSeconds = 5)
    {
        services.AddSingleton<ICircuitBreakerRegistry>(_ =>
            new CircuitBreakerRegistry(
                baseOpenDuration: TimeSpan.FromSeconds(circuitBreakerOpenSeconds),
                maxOpenDuration: TimeSpan.FromMinutes(5)));

        services.AddSingleton<IDeviceReader, DeviceReader>();
        services.AddSingleton<IPointValuePipeline, PointValuePipeline>();
        services.AddSingleton<IDataDispatcher, DataDispatcher>();
        services.AddSingleton<MeasurementWriteHost>();
        services.AddHostedService(sp =>sp.GetRequiredService<MeasurementWriteHost>());
        services.AddSingleton<SinkDispatcher>();
        services.AddHostedService(sp => sp.GetRequiredService<SinkDispatcher>());
        services.AddSingleton<IHealthReporter, HealthReporter>();
        // CircuitBreaker 监听 HealthMonitor 的 Online/Offline 信号
        services.AddSingleton<IDeviceHealthListener, CircuitBreakerHealthListener>();

        services.AddScoped<IDeviceCollector>(sp => new DeviceCollector(
            sp.GetRequiredService<IDeviceManager>(),
            sp.GetRequiredService<IDeviceReader>(),
            sp.GetRequiredService<IPointValuePipeline>(),
            sp.GetRequiredService<IDataDispatcher>(),
            sp.GetRequiredService<IHealthReporter>(),
            sp.GetRequiredService<ICircuitBreakerRegistry>(),
            sp.GetRequiredService<IDeviceHealthMonitor>(),
            sp.GetRequiredService<ILogger<DeviceCollector>>(),
            maxConcurrency));

        services.AddHostedService(sp => new CollectionEngine(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<NitroGateway.Host.GatewayLifecycle>(),
            TimeSpan.FromMilliseconds(intervalMs),
            sp.GetRequiredService<ILogger<CollectionEngine>>()));

        return services;
    }
}
