using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NitroGateway.Desktop;
using NitroGateway.Desktop.Services.Infrastructure;
using NitroGateway.Desktop.ViewModels;
using NitroGateway.DeviceManagement;
using NitroGateway.Domain.Devices;
using NitroGateway.Protocols;
using NitroGateway.Storage.Buffer;
using NitroGateway.Storage.TimeSeries;
using NitroGateway.Transport.MQTT;
using Xunit;

namespace NitroGateway.UnitTests.Desktop.ViewModels;

/// <summary>
/// 桌面壳主 ViewModel：一级「协议」目录统辖两个二级设备分区、目录点击只展开收起不高亮、
/// 设备总数=两分区之和、关闭时对两个分区都退订。依赖经真实 DI 图解析。
/// </summary>
public sealed class MainViewModelTests : IDisposable
{
    private ServiceProvider? _provider;

    public void Dispose() => _provider?.Dispose();

    [Fact]
    public void Nav_tree_starts_with_dashboard_then_protocol_group()
    {
        var main = CreateMainViewModel();

        // 仪表盘为第一项且默认选中（对齐 web 默认落仪表盘）
        Assert.False(main.NavTree[0].IsGroup);
        Assert.Equal("仪表盘", main.NavTree[0].Title);
        Assert.IsType<DashboardViewModel>(main.NavTree[0].ViewModel);
        Assert.Same(main.NavTree[0], main.SelectedNav);
        Assert.True(main.NavTree[0].IsSelected);
        Assert.Same(main.NavTree[0].ViewModel, main.CurrentViewModel);

        // 一级「协议」目录统辖两个二级设备分区
        var protocol = main.NavTree[1];
        Assert.True(protocol.IsGroup);
        Assert.Equal("协议", protocol.Title);
        Assert.Equal(2, protocol.Children.Count);
        Assert.Equal("Modbus / S7 设备", protocol.Children[0].Title);
        Assert.Equal("OPC UA 设备", protocol.Children[1].Title);

        var generic = Assert.IsType<DevicesViewModel>(protocol.Children[0].ViewModel);
        var opcUa = Assert.IsType<DevicesViewModel>(protocol.Children[1].ViewModel);
        Assert.False(generic.Scope.OpcUaOnly);
        Assert.True(opcUa.Scope.OpcUaOnly);

        Assert.False(main.NavTree[2].IsGroup);
        Assert.Equal("实时数据", main.NavTree[2].Title);
    }

    [Fact]
    public void Protocol_group_click_toggles_expansion_without_changing_selection()
    {
        var main = CreateMainViewModel();
        var dashboard = main.NavTree[0];
        var protocol = main.NavTree[1];

        Assert.True(protocol.IsExpanded);
        Assert.Same(dashboard, main.SelectedNav);

        protocol.IsSelected = true; // 模拟点击一级目录标题

        Assert.False(protocol.IsExpanded);          // 收起
        Assert.False(protocol.IsSelected);          // 目录本身不高亮
        Assert.Same(dashboard, main.SelectedNav);   // 内容区不变（仍在仪表盘）
        Assert.True(dashboard.IsSelected);          // 活动页保持高亮

        protocol.IsSelected = true;                 // 再点一次 → 重新展开
        Assert.True(protocol.IsExpanded);
    }

    [Fact]
    public void Selecting_second_partition_moves_highlight_and_content()
    {
        var main = CreateMainViewModel();
        var protocol = main.NavTree[1];
        var generic = protocol.Children[0];
        var opcUa = protocol.Children[1];

        opcUa.IsSelected = true;

        Assert.Same(opcUa, main.SelectedNav);
        Assert.True(opcUa.IsSelected);
        Assert.False(generic.IsSelected);          // 单高亮
        Assert.Same(opcUa.ViewModel, main.CurrentViewModel);
    }

    [Fact]
    public async Task Device_count_text_is_sum_of_both_partitions()
    {
        var cache = new StagedSnapshotCache();
        var main = CreateMainViewModel(cache);
        var generic = (DevicesViewModel)main.NavTree[1].Children[0].ViewModel;
        var opcUa = (DevicesViewModel)main.NavTree[1].Children[1].ViewModel;

        Assert.Equal("0", main.DeviceCountText);

        cache.EnqueueSuccess(TestDevices.Device("PLC-1"));
        await generic.RefreshCommand.ExecuteAsync(null);
        cache.EnqueueSuccess(OpcUaDevice("UA-1"));
        await opcUa.RefreshCommand.ExecuteAsync(null);

        Assert.Equal("2", main.DeviceCountText);
    }

    [Fact]
    public async Task Dispose_stops_device_count_updates()
    {
        var cache = new StagedSnapshotCache();
        var main = CreateMainViewModel(cache);
        var generic = (DevicesViewModel)main.NavTree[1].Children[0].ViewModel;

        cache.EnqueueSuccess(TestDevices.Device("PLC-1"));
        await generic.RefreshCommand.ExecuteAsync(null);
        Assert.Equal("1", main.DeviceCountText);

        main.Dispose();

        // 已退订：分区继续刷新不再改动状态栏计数
        cache.EnqueueSuccess(TestDevices.Device("PLC-1"), TestDevices.Device("PLC-2"));
        await generic.RefreshCommand.ExecuteAsync(null);
        Assert.Equal("1", main.DeviceCountText);
    }

    private MainViewModel CreateMainViewModel(IDeviceSnapshotCache? cache = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IForwardBuffer>(new StubForwardBuffer());
        services.AddSingleton<UiDispatcher>();
        services.AddSingleton<MqttConnectionOptions>();
        services.AddSingleton(cache ?? new StagedSnapshotCache());
        services.AddSingleton<IDeviceHealthMonitor>(new StubHealthMonitor());
        services.AddSingleton<IMeasurementStore>(new StagedMeasurementStore());
        services.AddNitroProtocol();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Persistence:ConnectionString"] = "Data Source=:memory:"
            })
            .Build();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddNitroDesktopShell(configuration);

        _provider = services.BuildServiceProvider();
        return _provider.GetRequiredService<MainViewModel>();
    }

    private static Device OpcUaDevice(string name) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Protocol = new ProtocolIdentifier { Name = "OPC UA" },
        Connection = new DeviceConnection { Endpoint = "opc.tcp://127.0.0.1:4840" }
    };
}
