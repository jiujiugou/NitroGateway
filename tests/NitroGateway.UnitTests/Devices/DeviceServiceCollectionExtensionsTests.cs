using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NitroGateway.DeviceManagement;
using NitroGateway.DeviceManagement.Events;
using NitroGateway.DeviceManagement.Listeners;
using NitroGateway.Security.Guard;
using Xunit;

namespace NitroGateway.UnitTests.Devices;

/// <summary>AddNitroDevice 的 DI 接线：核心单例可从容器解析。</summary>
public class DeviceServiceCollectionExtensionsTests
{
    [Fact]
    public void AddNitroDevice_RegistersCoreSingletons()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNitroDevice();
        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<IDeviceHealthMonitor>());
        Assert.NotNull(provider.GetRequiredService<IDeviceSnapshotCache>());
        Assert.NotNull(provider.GetRequiredService<RangeValidator>());
        Assert.NotNull(provider.GetRequiredService<RateLimitValidator>());
        Assert.NotNull(provider.GetRequiredService<ModeValidator>());
        Assert.NotNull(provider.GetRequiredService<WriteGuard>());
        Assert.NotNull(provider.GetRequiredService<IDeviceHealthListener>());

        // 依赖复杂（仓储/驱动池）的类型通过描述符断言，避免拉起完整依赖图。
        Assert.Contains(services, d => d.ServiceType == typeof(IDeviceManager));
        Assert.Contains(services, d => d.ServiceType == typeof(IPointManager));
        Assert.Contains(services, d => d.ServiceType == typeof(PointBatchService));
        Assert.Contains(services, d => d.ServiceType == typeof(IWriteService));
        Assert.Contains(services, d => d.ServiceType == typeof(IHostedService)
            && d.ImplementationType == typeof(HealthListenerRegistrar));
    }
}
