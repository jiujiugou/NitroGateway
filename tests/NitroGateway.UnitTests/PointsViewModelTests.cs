using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NitroGateway.Desktop.Services.Sync;
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
        var manager = new StubPointManager();
        manager.Seed(_deviceId, TestDevices.Point("温度"), TestDevices.Point("压力"));
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
        var outbox = new StubConfigSyncOutboxStore();
        var vm = CreateVm(manager, dialogs, outbox);

        await vm.AddCommand.ExecuteAsync(null);

        Assert.Empty(manager.Added);
        Assert.Equal(1, dialogs.EditPointCalls);
        Assert.Empty(outbox.Rows);
    }

    [Fact]
    public async Task AddPoint_records_outbox_row()
    {
        var manager = new StubPointManager();
        var dialogs = new StubDeviceDialogService { EditPointFillName = "温度" };
        var outbox = new StubConfigSyncOutboxStore();
        var vm = CreateVm(manager, dialogs, outbox);

        await vm.AddCommand.ExecuteAsync(null);

        var row = Assert.Single(outbox.Rows);
        Assert.Equal(ConfigSyncOutboxKind.Point, row.Kind);
        Assert.Equal(_deviceId, row.DeviceId);
        Assert.NotNull(row.PointId);
    }

    [Fact]
    public async Task EditPoint_records_outbox_row()
    {
        var point = TestDevices.Point("旧点位");
        var manager = new StubPointManager();
        manager.Seed(_deviceId, point);
        var dialogs = new StubDeviceDialogService { EditPointFillName = "新点位" };
        var outbox = new StubConfigSyncOutboxStore();
        var vm = CreateVm(manager, dialogs, outbox);
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.SelectedPoint = vm.Items[0];

        await vm.EditCommand.ExecuteAsync(null);

        var row = Assert.Single(outbox.Rows);
        Assert.Equal(ConfigSyncOutboxKind.Point, row.Kind);
        Assert.Equal(point.Id, row.PointId);
    }

    [Fact]
    public async Task EditPoint_updates_selected_point()
    {
        var point = TestDevices.Point("旧点位");
        var manager = new StubPointManager();
        manager.Seed(_deviceId, point);
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
        var manager = new StubPointManager();
        manager.Seed(_deviceId, point);
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
        var manager = new StubPointManager();
        manager.Seed(_deviceId, point);
        var dialogs = new StubDeviceDialogService { ConfirmResult = false };
        var vm = CreateVm(manager, dialogs);
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.SelectedPoint = vm.Items[0];

        await vm.DeleteCommand.ExecuteAsync(null);

        Assert.Empty(manager.Removed);
        Assert.Single(vm.Items);
    }

    [Fact]
    public async Task DeletePoint_records_tombstone_outbox_row()
    {
        var point = TestDevices.Point("要删的点位");
        var manager = new StubPointManager();
        manager.Seed(_deviceId, point);
        var dialogs = new StubDeviceDialogService();
        var outbox = new StubConfigSyncOutboxStore();
        var vm = CreateVm(manager, dialogs, outbox);
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.SelectedPoint = vm.Items[0];

        await vm.DeleteCommand.ExecuteAsync(null);

        var row = Assert.Single(outbox.Rows);
        Assert.Equal(ConfigSyncOutboxKind.PointDelete, row.Kind);
        Assert.Equal(point.Id, row.PointId);
    }

    [Fact]
    public async Task ImportCsv_imports_points_and_records_outbox()
    {
        var manager = new StubPointManager();
        var dialogs = new StubDeviceDialogService();
        var outbox = new StubConfigSyncOutboxStore();
        var csvFiles = new StubCsvFileService
        {
            PickImportResult = """
Name,Address,DataType,Access,Enabled,ScanIntervalMs,Deadband,ScaleFactor,ScaleOffset,Description
温度,40001,Float,ReadOnly,True,1000,0,1,0,测试
压力,40003,Float,ReadOnly,True,1000,0,1,0,测试
"""
        };
        var vm = CreateVm(manager, dialogs, outbox, csvFiles);

        await vm.ImportCsvCommand.ExecuteAsync(null);

        var imported = Assert.Single(manager.Imported);
        Assert.Equal(_deviceId, imported.DeviceId);
        Assert.Equal(2, imported.Points.Count);
        Assert.Equal(2, outbox.Records.Count(r => r.Kind == ConfigSyncOutboxKind.Point));
        Assert.Contains("已导入 2 个点位", vm.StatusText);
        Assert.Equal(2, vm.Items.Count);
    }

    [Fact]
    public async Task ImportCsv_cancel_does_not_import()
    {
        var manager = new StubPointManager();
        var dialogs = new StubDeviceDialogService();
        var csvFiles = new StubCsvFileService { PickImportResult = null };
        var vm = CreateVm(manager, dialogs, null, csvFiles);

        await vm.ImportCsvCommand.ExecuteAsync(null);

        Assert.Equal(1, csvFiles.PickCalls);
        Assert.Empty(manager.Imported);
    }

    [Fact]
    public async Task ImportCsv_invalid_csv_reports_error()
    {
        var manager = new StubPointManager();
        var dialogs = new StubDeviceDialogService();
        // 缺少必填列 DataType → 解析失败，不落库
        var csvFiles = new StubCsvFileService { PickImportResult = "Name,Address\n温度,40001" };
        var vm = CreateVm(manager, dialogs, null, csvFiles);

        await vm.ImportCsvCommand.ExecuteAsync(null);

        Assert.Empty(manager.Imported);
        Assert.Contains("导入失败", vm.StatusText);
    }

    [Fact]
    public async Task ExportCsv_saves_file_with_points()
    {
        var manager = new StubPointManager();
        manager.Seed(_deviceId, TestDevices.Point("温度"), TestDevices.Point("压力"));
        var dialogs = new StubDeviceDialogService();
        var csvFiles = new StubCsvFileService();
        var vm = CreateVm(manager, dialogs, null, csvFiles);

        await vm.ExportCsvCommand.ExecuteAsync(null);

        Assert.Equal(1, csvFiles.SaveCalls);
        Assert.Equal($"points_{_deviceId}.csv", csvFiles.LastSavedFileName);
        Assert.NotNull(csvFiles.LastSavedContent);
        Assert.Contains("温度", csvFiles.LastSavedContent);
        Assert.Contains("压力", csvFiles.LastSavedContent);
        Assert.Contains("已导出 2 个点位", vm.StatusText);
    }

    [Fact]
    public async Task ExportCsv_cancel_does_not_report_exported()
    {
        var manager = new StubPointManager();
        manager.Seed(_deviceId, TestDevices.Point("温度"));
        var dialogs = new StubDeviceDialogService();
        var csvFiles = new StubCsvFileService { SaveResult = false };
        var vm = CreateVm(manager, dialogs, null, csvFiles);

        await vm.ExportCsvCommand.ExecuteAsync(null);

        Assert.Equal(1, csvFiles.SaveCalls);
        Assert.DoesNotContain("已导出", vm.StatusText);
    }

    public void Dispose() => _provider?.Dispose();

    private PointsViewModel CreateVm(
        StubPointManager manager, StubDeviceDialogService dialogs,
        StubConfigSyncOutboxStore? outbox = null,
        StubCsvFileService? csvFiles = null)
    {
        var services = new ServiceCollection();
        services.AddScoped<IPointManager>(_ => manager);
        _provider = services.BuildServiceProvider();
        return new PointsViewModel(
            _deviceId, "车间 PLC",
            _provider.GetRequiredService<IServiceScopeFactory>(), dialogs,
            outbox ?? new StubConfigSyncOutboxStore(), csvFiles ?? new StubCsvFileService(),
            new PointBatchService(NullLogger<PointBatchService>.Instance),
            NullLogger<PointsViewModel>.Instance);
    }
}
