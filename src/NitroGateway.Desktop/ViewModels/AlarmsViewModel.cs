using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NitroGateway.Alarm.Domain;
using NitroGateway.Alarm.Repository;
using NitroGateway.Desktop.Services;
using NitroGateway.DeviceManagement;

namespace NitroGateway.Desktop.ViewModels;

/// <summary>
/// 告警页：最近 24 小时告警（活跃置顶展示），每 5s 自动刷新。
/// 设备名通过目录缓存映射（告警只携带 DeviceId）。
/// </summary>
public sealed partial class AlarmsViewModel : ObservableObject, IDisposable
{
    private const int HistoryHours = 24;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDeviceSnapshotCache _cache;
    private readonly UiDispatcher _ui;
    private readonly ILogger<AlarmsViewModel> _logger;
    private readonly DispatcherTimer _timer;

    public ObservableCollection<AlarmItem> Items { get; } = [];

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _statusText = "";

    public AlarmsViewModel(
        IServiceScopeFactory scopeFactory,
        IDeviceSnapshotCache cache,
        UiDispatcher ui,
        ILogger<AlarmsViewModel> logger)
    {
        _scopeFactory = scopeFactory;
        _cache = cache;
        _ui = ui;
        _logger = logger;

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
            var nameMap = await LoadDeviceNamesAsync();

            // ADR-027 P2-2：IAlarmRepository 为 Scoped（EF DbContext），
            // 单例 VM 每次刷新新建 scope，避免 DbContext/change tracker 跨轮询累积
            using var scope = _scopeFactory.CreateScope();
            var alarms = scope.ServiceProvider.GetRequiredService<IAlarmRepository>();

            var to = DateTime.UtcNow;
            var history = await alarms.QueryAsync(to.AddHours(-HistoryHours), to, 500);
            if (history.IsFailure)
            {
                StatusText = $"加载告警失败：{history.Error!.Message}";
                return;
            }

            var activeResult = await alarms.GetAllActiveAsync();
            var activeIds = activeResult.IsSuccess
                ? activeResult.Value!.Select(a => a.Id).ToHashSet()
                : new HashSet<Guid>();

            _ui.Post(() =>
            {
                Items.Clear();
                foreach (var alarm in history.Value!
                    .OrderByDescending(a => a.OccurredAt))
                {
                    Items.Add(new AlarmItem
                    {
                        Id = alarm.Id,
                        DeviceName = nameMap.GetValueOrDefault(alarm.DeviceId) ?? ShortId(alarm.DeviceId),
                        Severity = alarm.Severity,
                        State = activeIds.Contains(alarm.Id) ? AlarmState.Active : alarm.State,
                        Message = alarm.Message,
                        TriggerValue = alarm.TriggerValue,
                        Threshold = alarm.Threshold,
                        OccurredAt = alarm.OccurredAt
                    });
                }
                StatusText = $"最近 {HistoryHours} 小时共 {Items.Count} 条告警";
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "告警列表刷新失败");
            StatusText = "告警列表刷新失败";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task<Dictionary<Guid, string>> LoadDeviceNamesAsync()
    {
        var result = await _cache.GetAllAsync();
        return result.IsSuccess
            ? result.Value!.ToDictionary(d => d.Id, d => d.Name)
            : new Dictionary<Guid, string>();
    }

    private static string ShortId(Guid id) => id.ToString("N")[..8];

    public void Dispose() => _timer.Stop();
}

/// <summary>告警列表行</summary>
public sealed class AlarmItem
{
    public Guid Id { get; init; }
    public required string DeviceName { get; init; }
    public AlarmSeverity Severity { get; init; }
    public AlarmState State { get; init; }
    public required string Message { get; init; }
    public double TriggerValue { get; init; }
    public double Threshold { get; init; }
    public DateTime OccurredAt { get; init; }

    public string SeverityText => Severity.ToString();
    public string StateText => State switch
    {
        AlarmState.Active => "活跃",
        AlarmState.Acknowledged => "已确认",
        AlarmState.Resolved => "已恢复",
        _ => State.ToString()
    };
    public string OccurredText => OccurredAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
}
