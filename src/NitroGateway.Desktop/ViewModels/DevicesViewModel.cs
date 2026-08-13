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
using NitroGateway.Shared;

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
    private readonly IConfigSyncOutboxStore _outbox;
    private readonly ILogger<DevicesViewModel> _logger;
    private readonly DispatcherTimer _timer;

    /// <summary>设备行集合（UI 线程）</summary>
    public ObservableCollection<DeviceItem> Items { get; } = [];

    /// <summary>设备数变更事件（ADR-037 S11：MainViewModel 复用本事件展示设备数，不再重复查询目录）。</summary>
    public event EventHandler<int>? DeviceCountChanged;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditDeviceCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteDeviceCommand))]
    [NotifyCanExecuteChangedFor(nameof(ManagePointsCommand))]
    private DeviceItem? _selectedDevice;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isLoading;
    [ObservableProperty] private string _statusText = "";

    // ADR-038：设备管理统计卡（设备总数/在线/离线/点位数），随 RefreshAsync 重算
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private int _onlineCount;
    [ObservableProperty] private int _offlineCount;
    [ObservableProperty] private int _totalPoints;

    /// <summary>加载完成标志（ADR-037 S3）：刷新中禁用刷新按钮，避免无反馈的防重入吞点击。</summary>
    public bool IsIdle => !IsLoading;

    public DevicesViewModel(
        IDeviceSnapshotCache cache,
        IDeviceHealthMonitor health,
        UiDispatcher ui,
        EventBridge bridge,
        ILogger<DevicesViewModel> logger,
        IServiceScopeFactory scopeFactory,
        IDeviceDialogService dialogs,
        IConfigSyncOutboxStore outbox)
    {
        _cache = cache;
        _health = health;
        _ui = ui;
        _bridge = bridge;
        _scopeFactory = scopeFactory;
        _dialogs = dialogs;
        _outbox = outbox;
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

        // ADR-033 阶段 4：现场改动入 outbox，同步服务联网后上报中心
        await RecordOutboxAsync(() => _outbox.RecordDeviceAsync(result.Value!));

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

        // ADR-033 阶段 4：现场改动入 outbox（按 Id upsert，覆盖既有设备行）
        await RecordOutboxAsync(() => _outbox.RecordDeviceAsync(result.Value!));

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

        // ADR-033 阶段 4：删除发 tombstone 上报（设备删除行覆盖既有 upsert 行）
        await RecordOutboxAsync(() => _outbox.RecordDeviceDeleteAsync(selected.Id));

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
                // ADR-037 S7：先 diff 再增删改——既有行按 Id 原位更新（保留行实例/选中/滚动），
                // 仅新增/消失的设备做集合增删
                var incoming = devices.ToDictionary(d => d.Id);
                for (var i = Items.Count - 1; i >= 0; i--)
                {
                    if (!incoming.TryGetValue(Items[i].Id, out var device))
                    {
                        if (ReferenceEquals(SelectedDevice, Items[i]))
                            SelectedDevice = null;
                        Items.RemoveAt(i);
                        continue;
                    }

                    snapshots.TryGetValue(device.Id, out var snapshot);
                    ApplySnapshot(Items[i], device, snapshot);
                }

                foreach (var device in devices)
                {
                    if (Items.Any(item => item.Id == device.Id))
                        continue;
                    snapshots.TryGetValue(device.Id, out var snapshot);
                    var item = new DeviceItem { Id = device.Id, Name = device.Name };
                    ApplySnapshot(item, device, snapshot);
                    Items.Add(item);
                }
                TotalCount = Items.Count;
                OnlineCount = Items.Count(i => i.Status == DeviceStatus.Online);
                OfflineCount = Items.Count(i => i.Status == DeviceStatus.Offline);
                TotalPoints = Items.Sum(i => i.PointsCount);
                StatusText = $"共 {Items.Count} 台设备";
                DeviceCountChanged?.Invoke(this, Items.Count);
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

    /// <summary>把设备目录行 + 健康快照原位写入行模型（ADR-037 S7，属性可观察触发 UI 刷新）。</summary>
    private static void ApplySnapshot(DeviceItem item, Device device, DeviceHealthSnapshot? snapshot)
    {
        item.Name = device.Name;
        item.Protocol = BuildProtocolText(device.Protocol);
        item.Status = snapshot?.Status ?? device.Status;
        item.LastCollectionAt = snapshot?.LastCollectionAt;
        item.LastError = snapshot?.LastError;
        item.PointsCount = device.Points.Count;
        item.UnitId = device.Connection.Parameters.TryGetValue("UnitId", out var rawUnitId)
            && int.TryParse(rawUnitId?.ToString(), out var unitId)
                ? unitId
                : null;
    }

    public void Dispose()
    {
        _bridge.FrameReady -= OnFrame;
        _timer.Stop();
    }

    /// <summary>outbox 写入失败不阻断 UI 操作（采集/本地库照常），仅记调试日志。</summary>
    private async Task RecordOutboxAsync(Func<Task<OperationResult>> record)
    {
        var result = await record();
        if (result.IsFailure)
            _logger.LogDebug("配置同步 outbox 记录失败：{Error}", result.Error!.Message);
    }
}

/// <summary>设备列表行（ADR-037 S7：可观察对象，增量刷新时原位更新避免重建/选中丢失）</summary>
public sealed partial class DeviceItem : ObservableObject
{
    public Guid Id { get; init; }

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _protocol = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private DeviceStatus _status;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LastCollectionText))]
    private DateTime? _lastCollectionAt;

    [ObservableProperty] private string? _lastError;
    [ObservableProperty] private int _pointsCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UnitIdText))]
    private int? _unitId;

    public string StatusText => Status switch
    {
        DeviceStatus.Online => "在线",
        DeviceStatus.Offline => "离线",
        DeviceStatus.Error => "异常",
        DeviceStatus.Maintenance => "维护中",
        _ => "未知"
    };

    public string LastCollectionText => LastCollectionAt?.ToLocalTime().ToString("HH:mm:ss") ?? "—";

    /// <summary>从站号显示文本（非 Modbus/未配置显示占位符）</summary>
    public string UnitIdText => UnitId is null ? "—" : UnitId.Value.ToString();
}
