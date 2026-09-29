using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NitroGateway.Desktop.Messaging;
using NitroGateway.Desktop.Services.Infrastructure;
using NitroGateway.Desktop.Services.Sync;
using NitroGateway.Desktop.ViewModels;
using NitroGateway.DeviceManagement;
using NitroGateway.Domain.Devices;
using System.Text.Json;
using Xunit;

namespace NitroGateway.UnitTests;

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
        // docs/13：点位窗口需要设备协议（点位/批量生成的地址提示按协议区分）
        vm.SelectedDevice = new DeviceItem { Id = deviceId, Name = "PLC-1", Protocol = "Modbus (TCP)", ProtocolName = "Modbus" };

        vm.ManagePointsCommand.Execute(null);

        Assert.Equal((deviceId, "PLC-1", "Modbus"), Assert.Single(dialogs.ShowPointsCalls));
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

    [Fact]
    public async Task Timer_tick_triggers_refresh()
    {
        // 轮询节奏经 IUiTimer 注入：FakeUiTimer 手动触发一个周期等价 DispatcherTimer 到达
        var cache = new StagedSnapshotCache();
        cache.EnqueueSuccess(); // 构造时首次刷新
        var timer = new FakeUiTimer();
        var vm = CreateVm(cache, new StubDeviceManager(), new StubDeviceDialogService(), timer: timer);

        Assert.True(timer.IsStarted);
        Assert.Equal(1, timer.StartCalls);

        var device = TestDevices.Device("PLC-1");
        cache.EnqueueSuccess(device);
        timer.RaiseTick();

        await TestWait.UntilAsync(() => vm.Items.Count == 1);
        Assert.Equal("PLC-1", vm.Items[0].Name);
    }

    // ── 协议分区（对齐 web DeviceListView/OpcUaDeviceList 的按协议过滤）──

    [Fact]
    public async Task Refresh_generic_scope_excludes_opcua_devices()
    {
        var cache = new StagedSnapshotCache();
        cache.EnqueueSuccess(); // 构造时首次刷新
        cache.EnqueueSuccess(TestDevices.Device("PLC-1"), OpcUaDevice());
        var vm = CreateVm(cache, new StubDeviceManager(), new StubDeviceDialogService());

        await vm.RefreshCommand.ExecuteAsync(null);

        var item = Assert.Single(vm.Items);
        Assert.Equal("Modbus", item.ProtocolName);
    }

    [Fact]
    public async Task Refresh_opcua_scope_keeps_only_opcua_and_exposes_endpoint_and_security()
    {
        var cache = new StagedSnapshotCache();
        cache.EnqueueSuccess();
        cache.EnqueueSuccess(TestDevices.Device("PLC-1"), OpcUaDevice("UA-1"));
        var vm = CreateVm(cache, new StubDeviceManager(), new StubDeviceDialogService(),
            scope: DeviceListScope.OpcUa);

        await vm.RefreshCommand.ExecuteAsync(null);

        var item = Assert.Single(vm.Items);
        Assert.Equal("OPC UA", item.ProtocolName);
        Assert.Equal("opc.tcp://127.0.0.1:4840", item.Endpoint);
        Assert.Equal("None / None", item.SecuritySummary);
    }

    [Fact]
    public async Task Refresh_opcua_scope_security_summary_defaults_to_auto()
    {
        var cache = new StagedSnapshotCache();
        cache.EnqueueSuccess();
        var device = OpcUaDevice("UA-anon");
        device.Connection.Parameters.Clear();
        cache.EnqueueSuccess(device);
        var vm = CreateVm(cache, new StubDeviceManager(), new StubDeviceDialogService(),
            scope: DeviceListScope.OpcUa);

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal("自动 / 自动", Assert.Single(vm.Items).SecuritySummary);
    }

    [Fact]
    public async Task AddDevice_opcua_scope_locks_protocol_to_opcua_with_opc_tcp_default_endpoint()
    {
        var cache = new StagedSnapshotCache();
        cache.EnqueueSuccess();
        var dialogs = new StubDeviceDialogService { EditDeviceFillName = "UA-new" };
        var vm = CreateVm(cache, new StubDeviceManager(), dialogs, scope: DeviceListScope.OpcUa);
        cache.EnqueueSuccess(OpcUaDevice("UA-new"));

        await vm.AddDeviceCommand.ExecuteAsync(null);

        Assert.NotNull(dialogs.LastDeviceEditor);
        Assert.True(dialogs.LastDeviceEditor!.LockProtocol);
        Assert.Equal("OPC UA", dialogs.LastDeviceEditor.ProtocolName);
        Assert.StartsWith("opc.tcp://", dialogs.LastDeviceEditor.Endpoint, StringComparison.Ordinal);
        Assert.Equal("OPC UA", Assert.Single(vm.Items).ProtocolName);
    }

    [Fact]
    public async Task AddDevice_generic_scope_leaves_protocol_editable_with_modbus_default()
    {
        var cache = new StagedSnapshotCache();
        cache.EnqueueSuccess();
        var dialogs = new StubDeviceDialogService { EditDeviceFillName = "PLC-new" };
        var vm = CreateVm(cache, new StubDeviceManager(), dialogs);

        await vm.AddDeviceCommand.ExecuteAsync(null);

        Assert.NotNull(dialogs.LastDeviceEditor);
        Assert.False(dialogs.LastDeviceEditor!.LockProtocol);
        Assert.Equal("Modbus", dialogs.LastDeviceEditor.ProtocolName);
    }

    [Fact]
    public async Task Refresh_opcua_scope_reads_security_summary_from_json_element_parameters()
    {
        // 仓储反序列化产出 JsonElement（DomainMapper.DeserializeParams），非 string
        var cache = new StagedSnapshotCache();
        cache.EnqueueSuccess();
        var device = OpcUaDevice("UA-json");
        device.Connection.Parameters["SecurityPolicy"] = JsonSerializer.Deserialize<JsonElement>("\"Basic256Sha256\"");
        device.Connection.Parameters["SecurityMode"] = JsonSerializer.Deserialize<JsonElement>("\"SignAndEncrypt\"");
        cache.EnqueueSuccess(device);
        var vm = CreateVm(cache, new StubDeviceManager(), new StubDeviceDialogService(),
            scope: DeviceListScope.OpcUa);

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal("Basic256Sha256 / SignAndEncrypt", Assert.Single(vm.Items).SecuritySummary);
    }

    [Fact]
    public async Task Refresh_opcua_scope_security_summary_falls_back_per_missing_key()
    {
        // 半声明：只声明策略，模式未声明 → 未声明项显示「自动」
        var cache = new StagedSnapshotCache();
        cache.EnqueueSuccess();
        var device = OpcUaDevice("UA-partial");
        device.Connection.Parameters.Remove("SecurityMode");
        cache.EnqueueSuccess(device);
        var vm = CreateVm(cache, new StubDeviceManager(), new StubDeviceDialogService(),
            scope: DeviceListScope.OpcUa);

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal("None / 自动", Assert.Single(vm.Items).SecuritySummary);
    }

    [Fact]
    public async Task Refresh_opcua_scope_classifies_dirty_combo_box_protocol_name()
    {
        // 历史脏数据："System.Windows.Controls.ComboBoxItem: OPC UA" 必须仍归入 OPC UA 分区，
        // 否则该设备会从两个分区同时消失（既非 OPC UA，又被通用分区排除）
        var dirty = OpcUaDevice("UA-dirty");
        dirty.Protocol = new ProtocolIdentifier { Name = "System.Windows.Controls.ComboBoxItem: OPC UA" };

        var opcUaCache = new StagedSnapshotCache();
        opcUaCache.EnqueueSuccess();
        opcUaCache.EnqueueSuccess(dirty);
        var opcUaVm = CreateVm(opcUaCache, new StubDeviceManager(), new StubDeviceDialogService(),
            scope: DeviceListScope.OpcUa);

        await opcUaVm.RefreshCommand.ExecuteAsync(null);

        var item = Assert.Single(opcUaVm.Items);
        Assert.Equal("OPC UA", item.ProtocolName); // 展示值已归一化
    }

    [Fact]
    public async Task Refresh_generic_scope_excludes_dirty_opcua_protocol_name()
    {
        var dirty = OpcUaDevice("UA-dirty");
        dirty.Protocol = new ProtocolIdentifier { Name = "System.Windows.Controls.ComboBoxItem: OPC UA" };

        var cache = new StagedSnapshotCache();
        cache.EnqueueSuccess();
        cache.EnqueueSuccess(dirty);
        var vm = CreateVm(cache, new StubDeviceManager(), new StubDeviceDialogService());

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Empty(vm.Items);
    }

    [Fact]
    public async Task Refresh_opcua_scope_counts_only_its_own_partition()
    {
        var cache = new StagedSnapshotCache();
        cache.EnqueueSuccess();
        var modbus = TestDevices.Device("PLC-1", TestDevices.Point("P1"), TestDevices.Point("P2"));
        modbus.Status = DeviceStatus.Online;
        var opcUa = OpcUaDevice("UA-1");
        opcUa.Status = DeviceStatus.Offline;
        opcUa.AddPoint(TestDevices.Point("MyLevel"));
        cache.EnqueueSuccess(modbus, opcUa);
        var vm = CreateVm(cache, new StubDeviceManager(), new StubDeviceDialogService(),
            scope: DeviceListScope.OpcUa);

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(1, vm.TotalCount);
        Assert.Equal(0, vm.OnlineCount);
        Assert.Equal(1, vm.OfflineCount);
        Assert.Equal(1, vm.TotalPoints);
    }

    private DevicesViewModel CreateVm(
        IDeviceSnapshotCache cache, StubDeviceManager manager, StubDeviceDialogService dialogs,
        StubConfigSyncOutboxStore? outbox = null, IUiTimer? timer = null,
        DeviceListScope? scope = null)
    {
        var services = new ServiceCollection();
        services.AddScoped<IDeviceManager>(_ => manager);
        _provider = services.BuildServiceProvider();
        var provider = _provider;
        return new DevicesViewModel(
            cache, new FakeHealthMonitor(), new UiDispatcher(), _bridge,
            NullLogger<DevicesViewModel>.Instance,
            provider.GetRequiredService<IServiceScopeFactory>(), dialogs,
            outbox ?? new StubConfigSyncOutboxStore(), timer, scope);
    }

    /// <summary>构造 OPC UA 设备（含安全参数）用于协议分区过滤测试。</summary>
    private static Device OpcUaDevice(string name = "UA-1")
    {
        var device = new Device
        {
            Id = Guid.NewGuid(),
            Name = name,
            Protocol = new ProtocolIdentifier { Name = "OPC UA" },
            Connection = new DeviceConnection
            {
                Endpoint = "opc.tcp://127.0.0.1:4840",
                Parameters = new Dictionary<string, object>
                {
                    ["SecurityPolicy"] = "None",
                    ["SecurityMode"] = "None"
                }
            }
        };
        return device;
    }
}
