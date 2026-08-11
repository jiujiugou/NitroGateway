using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NitroGateway.Desktop.Messaging;
using NitroGateway.Desktop.Services;
using NitroGateway.Desktop.ViewModels;
using NitroGateway.DeviceManagement;
using NitroGateway.Domain.Devices;
using Xunit;

namespace NitroGateway.UnitTests;

/// <summary>
/// ADR-029 P1/P2：设备页增删改与点位管理命令——对话框取消不落库、
/// 保存走 IDeviceManager（Scoped，经 IServiceScopeFactory 解析）、成功后刷新列表。
/// </summary>
public sealed class DevicesViewModelTests : IDisposable
{
    /// <summary>帧间隔注入 1 小时，避免 EventBridge 后台循环干扰。</summary>
    private static readonly TimeSpan LongFrame = TimeSpan.FromHours(1);

    private readonly EventBridge _bridge;
    private ServiceProvider? _provider;

    public DevicesViewModelTests()
    {
        _bridge = new EventBridge(new StubForwardBuffer(), NullLogger<EventBridge>.Instance, LongFrame);
    }

    public void Dispose()
    {
        _bridge.Dispose();
        _provider?.Dispose();
    }

    [Fact]
    public async Task AddDevice_saves_via_manager_and_refreshes_list()
    {
        var cache = new StagedSnapshotCache();
        cache.EnqueueSuccess(); // 构造时首次刷新
        var manager = new StubDeviceManager();
        var dialogs = new StubDeviceDialogService { EditDeviceFillName = "1号车间 PLC" };
        var vm = CreateVm(cache, manager, dialogs);

        var saved = TestDevices.Device("1号车间 PLC");
        cache.EnqueueSuccess(saved); // 保存后刷新
        await vm.AddDeviceCommand.ExecuteAsync(null);

        var registered = Assert.Single(manager.Registered);
        Assert.Equal("1号车间 PLC", registered.Name);
        Assert.Equal(1, dialogs.EditDeviceCalls);
        Assert.Single(vm.Items);
        Assert.Equal(saved.Id, vm.Items[0].Id);
    }

    [Fact]
    public async Task AddDevice_cancel_does_not_call_manager()
    {
        var cache = new StagedSnapshotCache();
        cache.EnqueueSuccess();
        var manager = new StubDeviceManager();
        var dialogs = new StubDeviceDialogService { EditDeviceResult = false };
        var vm = CreateVm(cache, manager, dialogs);

        await vm.AddDeviceCommand.ExecuteAsync(null);

        Assert.Empty(manager.Registered);
        Assert.Equal(1, dialogs.EditDeviceCalls);
    }

    [Fact]
    public async Task AddDevice_failure_shows_error_status()
    {
        var cache = new StagedSnapshotCache();
        cache.EnqueueSuccess();
        var manager = new StubDeviceManager { FailNextRegister = true };
        var dialogs = new StubDeviceDialogService();
        var vm = CreateVm(cache, manager, dialogs);

        await vm.AddDeviceCommand.ExecuteAsync(null);

        Assert.Empty(manager.Registered);
        Assert.Contains("保存设备失败", vm.StatusText);
    }

    [Fact]
    public async Task EditDevice_loads_current_then_saves()
    {
        var cache = new StagedSnapshotCache();
        cache.EnqueueSuccess();
        var existing = TestDevices.Device("旧名称");
        var manager = new StubDeviceManager { GetResult = existing };
        var dialogs = new StubDeviceDialogService { EditDeviceFillName = "新名称" };
        var vm = CreateVm(cache, manager, dialogs);
        vm.SelectedDevice = new DeviceItem { Id = existing.Id, Name = "旧名称", Protocol = "Modbus" };

        cache.EnqueueSuccess(existing);
        await vm.EditDeviceCommand.ExecuteAsync(null);

        Assert.Equal(existing.Id, Assert.Single(manager.Registered).Id);
        Assert.Equal("新名称", Assert.Single(manager.Registered).Name);
    }

    [Fact]
    public async Task EditDevice_without_selection_is_noop()
    {
        var cache = new StagedSnapshotCache();
        cache.EnqueueSuccess();
        var manager = new StubDeviceManager();
        var dialogs = new StubDeviceDialogService();
        var vm = CreateVm(cache, manager, dialogs);

        await vm.EditDeviceCommand.ExecuteAsync(null);

        Assert.Empty(manager.Registered);
        Assert.Equal(0, dialogs.EditDeviceCalls);
    }

    [Fact]
    public async Task DeleteDevice_confirm_unregisters()
    {
        var cache = new StagedSnapshotCache();
        cache.EnqueueSuccess();
        var manager = new StubDeviceManager();
        var dialogs = new StubDeviceDialogService();
        var vm = CreateVm(cache, manager, dialogs);
        var deviceId = Guid.NewGuid();
        vm.SelectedDevice = new DeviceItem { Id = deviceId, Name = "要删的设备", Protocol = "Modbus" };

        cache.EnqueueSuccess();
        await vm.DeleteDeviceCommand.ExecuteAsync(null);

        Assert.Equal(deviceId, Assert.Single(manager.Unregistered));
        Assert.Equal(1, dialogs.ConfirmCalls);
    }

    [Fact]
    public async Task DeleteDevice_cancel_does_not_unregister()
    {
        var cache = new StagedSnapshotCache();
        cache.EnqueueSuccess();
        var manager = new StubDeviceManager();
        var dialogs = new StubDeviceDialogService { ConfirmResult = false };
        var vm = CreateVm(cache, manager, dialogs);
        vm.SelectedDevice = new DeviceItem { Id = Guid.NewGuid(), Name = "保留", Protocol = "Modbus" };

        await vm.DeleteDeviceCommand.ExecuteAsync(null);

        Assert.Empty(manager.Unregistered);
        Assert.Equal(1, dialogs.ConfirmCalls);
    }

    [Fact]
    public void ManagePoints_opens_dialog_with_device_id_and_name()
    {
        var cache = new StagedSnapshotCache();
        cache.EnqueueSuccess();
        var dialogs = new StubDeviceDialogService();
        var vm = CreateVm(cache, new StubDeviceManager(), dialogs);
        var deviceId = Guid.NewGuid();
        vm.SelectedDevice = new DeviceItem { Id = deviceId, Name = "PLC-1", Protocol = "Modbus" };

        vm.ManagePointsCommand.Execute(null);

        Assert.Equal((deviceId, "PLC-1"), Assert.Single(dialogs.ShowPointsCalls));
    }

    private DevicesViewModel CreateVm(
        IDeviceSnapshotCache cache, StubDeviceManager manager, StubDeviceDialogService dialogs)
    {
        var services = new ServiceCollection();
        services.AddScoped<IDeviceManager>(_ => manager);
        _provider = services.BuildServiceProvider();
        var provider = _provider;
        return new DevicesViewModel(
            cache, new FakeHealthMonitor(), new UiDispatcher(), _bridge,
            NullLogger<DevicesViewModel>.Instance,
            provider.GetRequiredService<IServiceScopeFactory>(), dialogs);
    }
}
