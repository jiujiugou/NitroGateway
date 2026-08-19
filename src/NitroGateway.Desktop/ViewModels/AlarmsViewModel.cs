using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NitroGateway.Alarm.Domain;
using NitroGateway.Alarm.Repository;
using NitroGateway.Desktop.Services.Infrastructure;

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
    private readonly IUiTimer _timer;

    public ObservableCollection<AlarmItem> Items { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isLoading;
    [ObservableProperty] private string _statusText = "";

    /// <summary>加载完成标志（ADR-037 S3）：刷新中禁用刷新按钮，避免无反馈的防重入吞点击。</summary>
    public bool IsIdle => !IsLoading;

    public AlarmsViewModel(
        IServiceScopeFactory scopeFactory,
        IDeviceSnapshotCache cache,
        UiDispatcher ui,
        ILogger<AlarmsViewModel> logger,
        IUiTimer? timer = null)
    {
        _scopeFactory = scopeFactory;
        _cache = cache;
        _ui = ui;
        _logger = logger;

        // 轮询节奏是 view 关注点：经 IUiTimer 注入，测试可手动触发；缺省用 WPF DispatcherTimer
        _timer = timer ?? new DispatcherUiTimer(TimeSpan.FromSeconds(5));
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
                // ADR-037 S7：先 diff 再增删改——既有行按 Id 原位更新（保留行实例/滚动），
                // 新告警按发生时间倒序插入顶部，消失的告警移除
                var ordered = history.Value!.OrderByDescending(a => a.OccurredAt).ToList();
                var incoming = ordered.ToDictionary(a => a.Id);

                for (var i = Items.Count - 1; i >= 0; i--)
                {
                    if (!incoming.TryGetValue(Items[i].Id, out var alarm))
                    {
                        Items.RemoveAt(i);
                        continue;
                    }
                    ApplyAlarm(Items[i], alarm, nameMap, activeIds);
                }

                foreach (var alarm in ordered)
                {
                    if (Items.Any(item => item.Id == alarm.Id))
                        continue;
                    var item = new AlarmItem { Id = alarm.Id };
                    ApplyAlarm(item, alarm, nameMap, activeIds);
                    var index = 0;
                    while (index < Items.Count && Items[index].OccurredAt >= alarm.OccurredAt)
                        index++;
                    Items.Insert(index, item);
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

    /// <summary>把告警域模型 + 活跃集合原位写入行模型（ADR-037 S7，属性可观察触发 UI 刷新）。</summary>
    private static void ApplyAlarm(
        AlarmItem item, NitroGateway.Alarm.Domain.Alarm alarm,
        IReadOnlyDictionary<Guid, string> nameMap, IReadOnlySet<Guid> activeIds)
    {
        item.DeviceName = nameMap.GetValueOrDefault(alarm.DeviceId) ?? ShortId(alarm.DeviceId);
        item.Severity = alarm.Severity;
        item.State = activeIds.Contains(alarm.Id) ? AlarmState.Active : alarm.State;
        item.Message = alarm.Message;
        item.TriggerValue = alarm.TriggerValue;
        item.Threshold = alarm.Threshold;
        item.OccurredAt = alarm.OccurredAt;
    }

    public void Dispose() => _timer.Stop();
}

/// <summary>告警列表行（ADR-037 S7：可观察对象，增量刷新时原位更新避免重建/滚动跳动）</summary>
public sealed partial class AlarmItem : ObservableObject
{
    public Guid Id { get; init; }

    [ObservableProperty] private string _deviceName = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SeverityText))]
    private AlarmSeverity _severity;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    private AlarmState _state;

    [ObservableProperty] private string _message = "";
    [ObservableProperty] private double _triggerValue;
    [ObservableProperty] private double _threshold;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OccurredText))]
    private DateTime _occurredAt;

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
