using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NitroGateway.Alarm.Repository;
using NitroGateway.Desktop.Services.Infrastructure;
using NitroGateway.DeviceManagement;
using NitroGateway.Domain.Devices;
using NitroGateway.Storage.Buffer;

namespace NitroGateway.Desktop.ViewModels;

/// <summary>
/// 仪表盘页（对齐 web <c>DashboardView.vue</c>）：6 个 KPI 卡 + 跨协议设备概览表，10s 轮询。
/// 数据全部来自进程内服务（设备快照缓存 / 健康监控 / 告警仓储 / 转发缓冲）；
/// 不显示实时点位值（实时值属「实时数据」页）。
/// </summary>
public sealed partial class DashboardViewModel : ObservableObject, IDisposable
{
    private readonly IDeviceSnapshotCache _cache;
    private readonly IDeviceHealthMonitor _health;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IForwardBuffer _buffer;
    private readonly UiDispatcher _ui;
    private readonly ILogger<DashboardViewModel> _logger;
    private readonly IUiTimer _timer;

    /// <summary>设备概览（全部协议，按名称排序）</summary>
    public ObservableCollection<DashboardDeviceItem> Devices { get; } = [];

    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private int _onlineCount;
    [ObservableProperty] private int _offlineCount;
    [ObservableProperty] private int _totalPoints;
    [ObservableProperty] private int _todayAlarmCount;
    [ObservableProperty] private int _bufferBacklog;
    [ObservableProperty] private string _statusText = "";

    public DashboardViewModel(
        IDeviceSnapshotCache cache,
        IDeviceHealthMonitor health,
        IServiceScopeFactory scopeFactory,
        IForwardBuffer buffer,
        UiDispatcher ui,
        ILogger<DashboardViewModel> logger,
        IUiTimer? timer = null)
    {
        _cache = cache;
        _health = health;
        _scopeFactory = scopeFactory;
        _buffer = buffer;
        _ui = ui;
        _logger = logger;

        // 轮询节奏是 view 关注点：经 IUiTimer 注入，测试可手动触发；缺省 WPF DispatcherTimer（对齐 web 10s）
        _timer = timer ?? new DispatcherUiTimer(TimeSpan.FromSeconds(10));
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();
        _ = RefreshAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
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
            // 健康快照优先，其次设备自身状态（与设备页同一口径）
            var statuses = devices.ToDictionary(
                d => d.Id,
                d => snapshots.GetValueOrDefault(d.Id)?.Status ?? d.Status);

            var todayAlarms = await CountTodayAlarmsAsync();
            var backlog = await _buffer.GetCountAsync();

            _ui.Post(() =>
            {
                Devices.Clear();
                foreach (var device in devices.OrderBy(d => d.Name, StringComparer.CurrentCulture))
                    Devices.Add(DashboardDeviceItem.From(device, statuses[device.Id]));

                TotalCount = devices.Count;
                OnlineCount = statuses.Values.Count(s => s == DeviceStatus.Online);
                // 对齐 web：离线或故障合并为一个 KPI
                OfflineCount = statuses.Values.Count(s => s is DeviceStatus.Offline or DeviceStatus.Error);
                TotalPoints = devices.Sum(d => d.Points.Count);
                TodayAlarmCount = todayAlarms;
                BufferBacklog = backlog;
                StatusText = $"共 {devices.Count} 台设备";
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "仪表盘刷新失败");
            StatusText = "仪表盘刷新失败";
        }
    }

    /// <summary>今日告警数（本地日零点起，UTC 口径与告警仓储一致）。</summary>
    private async Task<int> CountTodayAlarmsAsync()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var alarms = scope.ServiceProvider.GetRequiredService<IAlarmRepository>();
            var result = await alarms.CountOccurredSinceAsync(DateTime.Today.ToUniversalTime());
            return result.IsSuccess ? result.Value : 0;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "今日告警统计失败");
            return 0;
        }
    }

    public void Dispose() => _timer.Stop();
}

/// <summary>仪表盘设备概览行（跨协议，只读快照）。</summary>
public sealed class DashboardDeviceItem
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }

    /// <summary>协议显示文本（含方言，如 "Modbus (RTU)" / "S7 (TCP)" / "OPC UA"）</summary>
    public required string Protocol { get; init; }

    public required string Endpoint { get; init; }
    public required DeviceStatus Status { get; init; }
    public required int PointsCount { get; init; }

    public string StatusText => Status switch
    {
        DeviceStatus.Online => "在线",
        DeviceStatus.Offline => "离线",
        DeviceStatus.Error => "异常",
        DeviceStatus.Maintenance => "维护中",
        _ => "未知"
    };

    public static DashboardDeviceItem From(Device device, DeviceStatus status) => new()
    {
        Id = device.Id,
        Name = device.Name,
        Protocol = string.IsNullOrEmpty(device.Protocol.Dialect)
            ? device.Protocol.Name
            : $"{device.Protocol.Name} ({device.Protocol.Dialect})",
        Endpoint = device.Connection.Endpoint,
        Status = status,
        PointsCount = device.Points.Count
    };
}
