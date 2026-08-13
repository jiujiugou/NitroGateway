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
        var outbox = new StubConfigSyncOutboxStore();
        var vm = CreateVm(cache, manager, dialogs, outbox);

        await vm.AddDeviceCommand.ExecuteAsync(null);

        Assert.Empty(manager.Registered);
        Assert.Equal(1, dialogs.EditDeviceCalls);
        Assert.Empty(outbox.Rows);
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
    public async Task AddDevice_records_outbox_row()
    {
        var cache = new StagedSnapshotCache();
        cache.EnqueueSuccess();
        var manager = new StubDeviceManager();
        var dialogs = new StubDeviceDialogService { EditDeviceFillName = "新设备" };
        var outbox = new StubConfigSyncOutboxStore();
        var vm = CreateVm(cache, manager, dialogs, outbox);

        cache.EnqueueSuccess(TestDevices.Device("新设备"));
        await vm.AddDeviceCommand.ExecuteAsync(null);

        var row = Assert.Single(outbox.Rows);
        Assert.Equal(ConfigSyncOutboxKind.Device, row.Kind);
        // 负载引用实际注册的设备（Id 由 ViewModel 生成，非测试预置）
        Assert.Equal(Assert.Single(manager.Registered).Id, row.DeviceId);
        Assert.Null(row.PointId);
    }

    [Fact]
    public async Task EditDevice_records_outbox_row_replacing_previous()
    {
        var cache = new StagedSnapshotCache();
        cache.EnqueueSuccess();
        var existing = TestDevices.Device("旧名称");
        var manager = new StubDeviceManager { GetResult = existing };
        var dialogs = new StubDeviceDialogService { EditDeviceFillName = "新名称" };
        var outbox = new StubConfigSyncOutboxStore();
        var vm = CreateVm(cache, manager, dialogs, outbox);
        vm.SelectedDevice = new DeviceItem { Id = existing.Id, Name = "旧名称", Protocol = "Modbus" };
        cache.EnqueueSuccess(existing);

        await vm.EditDeviceCommand.ExecuteAsync(null);

        var row = Assert.Single(outbox.Rows);
        Assert.Equal(ConfigSyncOutboxKind.Device, row.Kind);
        Assert.Equal(existing.Id, row.DeviceId);
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
    public async Task DeleteDevice_records_tombstone_outbox_row()
    {
        var cache = new StagedSnapshotCache();
        cache.EnqueueSuccess();
        var manager = new StubDeviceManager();
        var dialogs = new StubDeviceDialogService();
        var outbox = new StubConfigSyncOutboxStore();
        var vm = CreateVm(cache, manager, dialogs, outbox);
        var deviceId = Guid.NewGuid();
        vm.SelectedDevice = new DeviceItem { Id = deviceId, Name = "要删的设备", Protocol = "Modbus" };

        cache.EnqueueSuccess();
        await vm.DeleteDeviceCommand.ExecuteAsync(null);

        var row = Assert.Single(outbox.Rows);
        Assert.Equal(ConfigSyncOutboxKind.DeviceDelete, row.Kind);
        Assert.Equal(deviceId, row.DeviceId);
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

    [Fact]
    public async Task Refresh_shows_unit_id_from_parameters()
    {
        var cache = new StagedSnapshotCache();
        cache.EnqueueSuccess(); // 构造时首次刷新
        var device = TestDevices.Device("RTU-1");
        device.Connection.Parameters["UnitId"] = 7;
        cache.EnqueueSuccess(device);
        var vm = CreateVm(cache, new StubDeviceManager(), new StubDeviceDialogService());

        await vm.RefreshCommand.ExecuteAsync(null);

        var item = Assert.Single(vm.Items);
        Assert.Equal(7, item.UnitId);
        Assert.Equal("7", item.UnitIdText);
    }

    [Fact]
    public async Task Refresh_unit_id_dash_when_parameter_missing()
    {
        var cache = new StagedSnapshotCache();
        cache.EnqueueSuccess(); // 构造时首次刷新
        cache.EnqueueSuccess(TestDevices.Device("S7-1"));
        var vm = CreateVm(cache, new StubDeviceManager(), new StubDeviceDialogService());

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal("—", Assert.Single(vm.Items).UnitIdText);
    }

    // ===== ADR-037 S7：增量刷新保留行实例/选中/滚动 =====

    [Fact]
    public async Task Refresh_reuses_existing_row_instances_and_preserves_selection()
    {
        var cache = new StagedSnapshotCache();
        cache.EnqueueSuccess(); // 构造时首次刷新
        var device = TestDevices.Device("PLC-1");
        cache.EnqueueSuccess(device);
        var vm = CreateVm(cache, new StubDeviceManager(), new StubDeviceDialogService());

        await vm.RefreshCommand.ExecuteAsync(null);

        var first = Assert.Single(vm.Items);
        vm.SelectedDevice = first;

        cache.EnqueueSuccess(device);
        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Single(vm.Items);
        Assert.Same(first, vm.Items[0]);
        Assert.Same(first, vm.SelectedDevice);
    }

    [Fact]
    public async Task Refresh_updates_existing_row_in_place()
    {
        var cache = new StagedSnapshotCache();
        cache.EnqueueSuccess();
        var device = TestDevices.Device("PLC-1");
        cache.EnqueueSuccess(device);
        var vm = CreateVm(cache, new StubDeviceManager(), new StubDeviceDialogService());

        await vm.RefreshCommand.ExecuteAsync(null);

        var first = vm.Items[0];
        device.Name = "PLC-1-改";
        device.AddPoint(TestDevices.Point("P1"));
        cache.EnqueueSuccess(device);

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Single(vm.Items);
        Assert.Same(first, vm.Items[0]);
        Assert.Equal("PLC-1-改", vm.Items[0].Name);
        Assert.Equal(1, vm.Items[0].PointsCount);
    }

    [Fact]
    public async Task Refresh_removes_missing_device_and_clears_selection()
    {
        var cache = new StagedSnapshotCache();
        cache.EnqueueSuccess();
        var kept = TestDevices.Device("保留");
        var removed = TestDevices.Device("删除");
        cache.EnqueueSuccess(kept, removed);
        var vm = CreateVm(cache, new StubDeviceManager(), new StubDeviceDialogService());

        await vm.RefreshCommand.ExecuteAsync(null);

        var keptRow = vm.Items.Single(i => i.Id == kept.Id);
        vm.SelectedDevice = vm.Items.Single(i => i.Id == removed.Id);

        cache.EnqueueSuccess(kept);
        await vm.RefreshCommand.ExecuteAsync(null);

        var survivor = Assert.Single(vm.Items);
        Assert.Same(keptRow, survivor);
        Assert.Null(vm.SelectedDevice);
    }

    [Fact]
    public async Task Refresh_raises_device_count_changed()
    {
        // ADR-037 S11：MainViewModel 复用本事件展示设备数，不再重复查询目录
        var cache = new StagedSnapshotCache();
        cache.EnqueueSuccess();
        var device = TestDevices.Device("PLC-1");
        cache.EnqueueSuccess(device);
        var vm = CreateVm(cache, new StubDeviceManager(), new StubDeviceDialogService());
        int? raised = null;
        vm.DeviceCountChanged += (_, count) => raised = count;

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task Refresh_computes_total_online_offline_and_point_counts()
    {
        // ADR-038：统计卡数据源——设备总数/在线/离线/点位数在刷新 diff 完成后重算
        var cache = new StagedSnapshotCache();
        cache.EnqueueSuccess();
        var online = TestDevices.Device("在线设备", TestDevices.Point("P1"), TestDevices.Point("P2"));
        online.Status = DeviceStatus.Online;
        var offline = TestDevices.Device("离线设备");
        offline.Status = DeviceStatus.Offline;
        var unknown = TestDevices.Device("未知设备", TestDevices.Point("P3"));
        unknown.Status = DeviceStatus.Unknown;
        cache.EnqueueSuccess(online, offline, unknown);
        var vm = CreateVm(cache, new StubDeviceManager(), new StubDeviceDialogService());

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(3, vm.TotalCount);
        Assert.Equal(1, vm.OnlineCount);
        Assert.Equal(1, vm.OfflineCount);
        Assert.Equal(3, vm.TotalPoints);
    }

    private DevicesViewModel CreateVm(
        IDeviceSnapshotCache cache, StubDeviceManager manager, StubDeviceDialogService dialogs,
        StubConfigSyncOutboxStore? outbox = null)
    {
        var services = new ServiceCollection();
        services.AddScoped<IDeviceManager>(_ => manager);
        _provider = services.BuildServiceProvider();
        var provider = _provider;
        return new DevicesViewModel(
            cache, new FakeHealthMonitor(), new UiDispatcher(), _bridge,
            NullLogger<DevicesViewModel>.Instance,
            provider.GetRequiredService<IServiceScopeFactory>(), dialogs,
            outbox ?? new StubConfigSyncOutboxStore());
    }
}
