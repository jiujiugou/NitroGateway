using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NitroGateway.Desktop.Messaging;
using NitroGateway.Desktop.Services;
using NitroGateway.DeviceManagement;
using NitroGateway.Domain.Devices;

namespace NitroGateway.Desktop.ViewModels;

/// <summary>
/// 设备列表页：设备目录（含点位数）+ HealthMonitor 实时状态。
/// 每 5s 自动刷新，设备健康变更帧触发即时刷新。
/// </summary>
public sealed partial class DevicesViewModel : ObservableObject, IDisposable
{
    private readonly IDeviceSnapshotCache _cache;
    private readonly IDeviceHealthMonitor _health;
    private readonly UiDispatcher _ui;
    private readonly EventBridge _bridge;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDeviceDialogService _dialogs;
    private readonly ILogger<DevicesViewModel> _logger;
    private readonly DispatcherTimer _timer;

    /// <summary>设备行集合（UI 线程）</summary>
    public ObservableCollection<DeviceItem> Items { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditDeviceCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteDeviceCommand))]
    [NotifyCanExecuteChangedFor(nameof(ManagePointsCommand))]
    private DeviceItem? _selectedDevice;

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _statusText = "";

    public DevicesViewModel(
        IDeviceSnapshotCache cache,
        IDeviceHealthMonitor health,
        UiDispatcher ui,
        EventBridge bridge,
        ILogger<DevicesViewModel> logger,
        IServiceScopeFactory scopeFactory,
        IDeviceDialogService dialogs)
    {
        _cache = cache;
        _health = health;
        _ui = ui;
        _bridge = bridge;
        _scopeFactory = scopeFactory;
        _dialogs = dialogs;
        _logger = logger;

        _bridge.FrameReady += OnFrame;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();
        _ = RefreshAsync();
    }

    private bool HasSelection => SelectedDevice is not null;

    /// <summary>新增设备：表单保存后经 IDeviceManager.RegisterAsync（按 Id upsert）落库并刷新</summary>
    [RelayCommand]
    private async Task AddDeviceAsync()
    {
        var editor = new DeviceEditor { Id = Guid.NewGuid() };
        if (!_dialogs.EditDevice(editor))
            return;

        using var scope = _scopeFactory.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<IDeviceManager>();
        var result = await manager.RegisterAsync(editor.ToDevice());
        if (result.IsFailure)
        {
            StatusText = $"保存设备失败：{result.Error!.Message}";
            return;
        }

        StatusText = $"设备「{editor.Name}」已保存";
        await RefreshAsync();
    }

    /// <summary>编辑设备：加载现有配置回填表单，保存后 upsert</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task EditDeviceAsync()
    {
        var selected = SelectedDevice;
        if (selected is null)
            return;

        using var scope = _scopeFactory.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<IDeviceManager>();
        var loaded = await manager.GetAsync(selected.Id);
        if (loaded.IsFailure)
        {
            StatusText = $"加载设备失败：{loaded.Error!.Message}";
            return;
        }

        var editor = DeviceEditor.FromDevice(loaded.Value!);
        if (!_dialogs.EditDevice(editor))
            return;

        var result = await manager.RegisterAsync(editor.ToDevice());
        if (result.IsFailure)
        {
            StatusText = $"保存设备失败：{result.Error!.Message}";
            return;
        }

        StatusText = $"设备「{editor.Name}」已更新";
        await RefreshAsync();
    }

    /// <summary>删除设备：确认后 UnregisterAsync（级联删除点位，ADR-021 P3-4 契约）</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task DeleteDeviceAsync()
    {
        var selected = SelectedDevice;
        if (selected is null)
            return;

        if (!_dialogs.Confirm("删除设备", $"确定删除设备「{selected.Name}」及其全部点位？"))
            return;

        using var scope = _scopeFactory.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<IDeviceManager>();
        var result = await manager.UnregisterAsync(selected.Id);
        if (result.IsFailure)
        {
            StatusText = $"删除设备失败：{result.Error!.Message}";
            return;
        }

        StatusText = $"设备「{selected.Name}」已删除";
        await RefreshAsync();
    }

    /// <summary>点位管理：打开模态点位窗口（ADR-029 P2），关闭后刷新计数</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void ManagePoints()
    {
        var selected = SelectedDevice;
        if (selected is null)
            return;

        _dialogs.ShowPoints(selected.Id, selected.Name);
        _ = RefreshAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsLoading)
            return;

        IsLoading = true;
        try
        {
            var devicesResult = await _cache.GetAllAsync();
            if (devicesResult.IsFailure)
            {
                StatusText = $"加载设备失败：{devicesResult.Error!.Message}";
                return;
            }

            var devices = devicesResult.Value!;
            var snapshots = _health.GetAllSnapshots().ToDictionary(s => s.DeviceId);

            _ui.Post(() =>
            {
                Items.Clear();
                foreach (var device in devices)
                {
                    snapshots.TryGetValue(device.Id, out var snapshot);
                    Items.Add(new DeviceItem
                    {
                        Id = device.Id,
                        Name = device.Name,
                        Protocol = BuildProtocolText(device.Protocol),
                        Status = snapshot?.Status ?? device.Status,
                        LastCollectionAt = snapshot?.LastCollectionAt,
                        LastError = snapshot?.LastError,
                        PointsCount = device.Points.Count
                    });
                }
                StatusText = $"共 {Items.Count} 台设备";
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "设备列表刷新失败");
            StatusText = "设备列表刷新失败";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void OnFrame(UiFrame frame)
    {
        if (frame.HealthChanges.Count == 0)
            return;
        _ = RefreshAsync();
    }

    private static string BuildProtocolText(ProtocolIdentifier protocol)
    {
        var text = protocol.Name;
        if (!string.IsNullOrEmpty(protocol.Dialect))
            text += $" ({protocol.Dialect})";
        return text;
    }

    public void Dispose()
    {
        _bridge.FrameReady -= OnFrame;
        _timer.Stop();
    }
}

/// <summary>设备列表行（不可变快照，每次刷新重建）</summary>
public sealed class DeviceItem
{
    public Guid Id { get; init; }
    public required string Name { get; init; }
    public required string Protocol { get; init; }
    public DeviceStatus Status { get; init; }
    public DateTime? LastCollectionAt { get; init; }
    public string? LastError { get; init; }
    public int PointsCount { get; init; }

    public string StatusText => Status switch
    {
        DeviceStatus.Online => "在线",
        DeviceStatus.Offline => "离线",
        DeviceStatus.Error => "异常",
        DeviceStatus.Maintenance => "维护中",
        _ => "未知"
    };

    public string LastCollectionText => LastCollectionAt?.ToLocalTime().ToString("HH:mm:ss") ?? "—";
}
