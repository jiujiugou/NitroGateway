using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
    private readonly ILogger<DevicesViewModel> _logger;
    private readonly DispatcherTimer _timer;

    /// <summary>设备行集合（UI 线程）</summary>
    public ObservableCollection<DeviceItem> Items { get; } = [];

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _statusText = "";

    public DevicesViewModel(
        IDeviceSnapshotCache cache,
        IDeviceHealthMonitor health,
        UiDispatcher ui,
        EventBridge bridge,
        ILogger<DevicesViewModel> logger)
    {
        _cache = cache;
        _health = health;
        _ui = ui;
        _bridge = bridge;
        _logger = logger;

        _bridge.FrameReady += OnFrame;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();
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
