using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NitroGateway.Desktop.Services;
using NitroGateway.Desktop.ViewModels;
using NitroGateway.DeviceManagement;
using NitroGateway.Domain.Devices;
using Xunit;

namespace NitroGateway.UnitTests;

/// <summary>
/// ADR-029 P2：点位管理窗口增删改——对话框取消不落库、
/// 增删改走 IPointManager（Scoped，经 IServiceScopeFactory 解析）、成功后刷新列表。
/// </summary>
public sealed class PointsViewModelTests : IDisposable
{
    private readonly Guid _deviceId = Guid.NewGuid();
    private ServiceProvider? _provider;

    [Fact]
    public void DeviceName_exposed_for_window_title()
    {
        var vm = CreateVm(new StubPointManager(), new StubDeviceDialogService());
        Assert.Equal("车间 PLC", vm.DeviceName);
    }

    [Fact]
    public async Task RefreshAsync_loads_points()
    {
        var manager = new StubPointManager
        {
            Points = { TestDevices.Point("温度"), TestDevices.Point("压力") }
        };
        var vm = CreateVm(manager, new StubDeviceDialogService());

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.Items.Count);
        Assert.Contains("共 2 个点位", vm.StatusText);
    }

    [Fact]
    public async Task AddPoint_saves_via_manager_and_refreshes()
    {
        var manager = new StubPointManager();
        var dialogs = new StubDeviceDialogService { EditPointFillName = "温度" };
        var vm = CreateVm(manager, dialogs);

        await vm.AddCommand.ExecuteAsync(null);

        var added = Assert.Single(manager.Added);
        Assert.Equal(_deviceId, added.DeviceId);
        Assert.Equal("温度", added.Point.Name);
        Assert.Single(vm.Items);
    }

    [Fact]
    public async Task AddPoint_cancel_does_not_call_manager()
    {
        var manager = new StubPointManager();
        var dialogs = new StubDeviceDialogService { EditPointResult = false };
        var vm = CreateVm(manager, dialogs);

        await vm.AddCommand.ExecuteAsync(null);

        Assert.Empty(manager.Added);
        Assert.Equal(1, dialogs.EditPointCalls);
    }

    [Fact]
    public async Task EditPoint_updates_selected_point()
    {
        var point = TestDevices.Point("旧点位");
        var manager = new StubPointManager { Points = { point } };
        var dialogs = new StubDeviceDialogService { EditPointFillName = "新点位" };
        var vm = CreateVm(manager, dialogs);
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.SelectedPoint = vm.Items[0];

        await vm.EditCommand.ExecuteAsync(null);

        var updated = Assert.Single(manager.Updated);
        Assert.Equal(point.Id, updated.Point.Id);
        Assert.Equal("新点位", updated.Point.Name);
        Assert.Equal("新点位", vm.Items[0].Name);
    }

    [Fact]
    public async Task DeletePoint_confirm_removes()
    {
        var point = TestDevices.Point("要删的点位");
        var manager = new StubPointManager { Points = { point } };
        var dialogs = new StubDeviceDialogService();
        var vm = CreateVm(manager, dialogs);
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.SelectedPoint = vm.Items[0];

        await vm.DeleteCommand.ExecuteAsync(null);

        Assert.Equal((_deviceId, point.Id), Assert.Single(manager.Removed));
        Assert.Empty(vm.Items);
        Assert.Equal(1, dialogs.ConfirmCalls);
    }

    [Fact]
    public async Task DeletePoint_cancel_does_not_remove()
    {
        var point = TestDevices.Point("保留");
        var manager = new StubPointManager { Points = { point } };
        var dialogs = new StubDeviceDialogService { ConfirmResult = false };
        var vm = CreateVm(manager, dialogs);
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.SelectedPoint = vm.Items[0];

        await vm.DeleteCommand.ExecuteAsync(null);

        Assert.Empty(manager.Removed);
        Assert.Single(vm.Items);
    }

    public void Dispose() => _provider?.Dispose();

    private PointsViewModel CreateVm(StubPointManager manager, StubDeviceDialogService dialogs)
    {
        var services = new ServiceCollection();
        services.AddScoped<IPointManager>(_ => manager);
        _provider = services.BuildServiceProvider();
        return new PointsViewModel(
            _deviceId, "车间 PLC",
            _provider.GetRequiredService<IServiceScopeFactory>(), dialogs,
            NullLogger<PointsViewModel>.Instance);
    }
}
