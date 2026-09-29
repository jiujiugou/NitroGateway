using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NitroGateway.Desktop.Messaging;
using NitroGateway.Desktop.Services.Dialogs;
using NitroGateway.Desktop.Services.Infrastructure;
using NitroGateway.Desktop.Services.Sync;
using NitroGateway.Desktop.ViewModels;
using NitroGateway.DeviceManagement;
using Xunit;

namespace NitroGateway.UnitTests;

/// <summary>
/// 协议分区工厂：同一套 <see cref="DevicesViewModel"/> 按分区创建多份，必须相互独立
/// （各自的 Items 与 Scope），否则两个设备页会共用一份列表导致过滤失效。
/// </summary>
public sealed class DevicesViewModelFactoryTests : IDisposable
{
    private readonly EventBridge _bridge =
        new(new StubForwardBuffer(), NullLogger<EventBridge>.Instance, TimeSpan.FromHours(1));

    private ServiceProvider? _provider;

    public void Dispose()
    {
        _provider?.Dispose();
        _bridge.Dispose();
    }

    [Fact]
    public void Create_twice_yields_independent_view_models()
    {
        var factory = CreateFactory();

        var generic = factory.Create(DeviceListScope.Generic);
        var opcUa = factory.Create(DeviceListScope.OpcUa);

        Assert.NotSame(generic, opcUa);
        Assert.NotSame(generic.Items, opcUa.Items);
        Assert.Equal("Modbus / S7 设备", generic.Scope.Title);
        Assert.False(generic.Scope.OpcUaOnly);
        Assert.Equal("OPC UA 设备", opcUa.Scope.Title);
        Assert.True(opcUa.Scope.OpcUaOnly);

        generic.Dispose();
        opcUa.Dispose();
    }

    private DevicesViewModelFactory CreateFactory()
    {
        // 对话框/outbox 由工厂从 scope 解析（避免单例捕获 Scoped 依赖），故需在容器内注册
        var services = new ServiceCollection();
        services.AddSingleton<IDeviceSnapshotCache>(new StagedSnapshotCache());
        services.AddSingleton<IDeviceHealthMonitor>(new StubHealthMonitor());
        services.AddSingleton<IDeviceDialogService>(new StubDeviceDialogService());
        services.AddSingleton<IConfigSyncOutboxStore>(new StubConfigSyncOutboxStore());
        _provider = services.BuildServiceProvider();

        return new DevicesViewModelFactory(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            _provider.GetRequiredService<IDeviceSnapshotCache>(),
            _provider.GetRequiredService<IDeviceHealthMonitor>(),
            new UiDispatcher(),
            _bridge,
            NullLogger<DevicesViewModel>.Instance);
    }
}
