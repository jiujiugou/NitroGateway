using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using NitroGateway.Desktop.Services.Infrastructure;

namespace NitroGateway.Desktop.ViewModels;

/// <summary>
/// 系统监控页：进程内直接抓取 prometheus-net 指标（本地，无需外部 Prometheus），
/// 把累计 Counter 换算成速率、直方图换算成 P95，供现场直观衡量采集 / 转发链路。
/// 只反映本进程当前状态与短时趋势；历史与告警仍交由 Prometheus 负责。
/// </summary>
public sealed partial class MetricsViewModel : ObservableObject, IDisposable
{
    private static readonly CultureInfo Ci = CultureInfo.CurrentCulture;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly IMetricsSource _source;
    private readonly UiDispatcher _ui;
    private readonly ILogger<MetricsViewModel> _logger;
    private readonly IUiTimer _timer;

    private MetricsSnapshot? _prev;
    private DateTime _prevAtUtc;
    private bool _refreshing;

    /// <summary>按主题分组的指标行。</summary>
    public ObservableCollection<MetricGroup> Groups { get; } = [];

    [ObservableProperty] private string _statusText = "首次采样中…";
    [ObservableProperty] private string _lastUpdatedText = "—";

    public MetricsViewModel(
        IMetricsSource source,
        UiDispatcher ui,
        ILogger<MetricsViewModel> logger,
        IUiTimer? timer = null)
    {
        _source = source;
        _ui = ui;
        _logger = logger;
        BuildGroups();

        _timer = timer ?? new DispatcherUiTimer(PollInterval);
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();
        _ = RefreshAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (_refreshing)
            return;
        _refreshing = true;
        try
        {
            var text = await _source.ScrapeAsync();
            var now = DateTime.UtcNow;
            var snapshot = MetricsSnapshot.Parse(text);
            var prev = _prev;
            var elapsed = prev is null ? 0 : (now - _prevAtUtc).TotalSeconds;

            var updates = new List<(MetricRow Row, string Value)>();
            foreach (var group in Groups)
            {
                foreach (var row in group.Rows)
                    updates.Add((row, row.Compute(snapshot, prev, elapsed)));
            }

            var sampleCount = snapshot.SampleCount;
            _prev = snapshot;
            _prevAtUtc = now;

            _ui.Post(() =>
            {
                foreach (var (row, value) in updates)
                    row.Value = value;
                LastUpdatedText = DateTime.Now.ToString("HH:mm:ss", Ci);
                StatusText = $"已采样 {sampleCount} 条指标 · 每 {PollInterval.TotalSeconds:0} 秒刷新";
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "抓取进程内指标失败");
            _ui.Post(() => StatusText = "抓取失败：" + ex.Message);
        }
        finally
        {
            _refreshing = false;
        }
    }

    public void Dispose() => _timer.Stop();

    // ── 分组 / 行构建 ──

    private void BuildGroups()
    {
        Groups.Add(new MetricGroup("采集",
        [
            new MetricRow("采集点速率", "点/秒", Rate("nitro_collection_points_total")),
            new MetricRow("死区抑制点速率", "点/秒", Rate("nitro_deadband_suppressed_points_total")),
            new MetricRow("数据新鲜度", "秒", FreshnessSeconds()),
            new MetricRow("采集设备速率", "次/秒", Rate("nitro_collection_total")),
            new MetricRow("失败速率", "次/秒", Rate("nitro_collection_total", Status("failure"))),
            new MetricRow("采集轮耗时 P95", "ms", HistP95("nitro_collection_duration_ms")),
            new MetricRow("采集跳过速率", "次/秒", Rate("nitro_collection_skipped_total")),
            new MetricRow("设备在线", "台", Gauge("nitro_devices_online")),
            new MetricRow("待采设备", "台", Gauge("nitro_devices_available")),
            new MetricRow("熔断中设备", "台", CountOpen()),
        ]));

        Groups.Add(new MetricGroup("转发",
        [
            new MetricRow("转发点速率", "点/秒", Rate("nitro_forward_points_total", Status("success"))),
            new MetricRow("丢弃点速率", "点/秒", Rate("nitro_forward_points_total", Status("dropped"))),
            new MetricRow("成功速率", "批/秒", Rate("nitro_forward_total", Status("success"))),
            new MetricRow("失败速率", "批/秒", Rate("nitro_forward_total", Status("failure"))),
            new MetricRow("丢弃速率", "批/秒", Rate("nitro_forward_total", Status("dropped"))),
            new MetricRow("缓冲积压", "批", Gauge("nitro_buffer_backlog")),
            new MetricRow("在途发布", "条", Gauge("nitro_forward_inflight")),
            new MetricRow("磁盘跳过速率", "次/秒", Rate("nitro_dispatch_skipped_total")),
            new MetricRow("转发轮耗时 P95", "ms", HistP95("nitro_forward_round_duration_ms")),
            new MetricRow("MQTT 发布 P95", "ms", HistP95("nitro_mqtt_publish_duration_ms")),
        ]));

        Groups.Add(new MetricGroup("连接",
        [
            new MetricRow("MQTT 状态", "", MqttState()),
            new MetricRow("入队被拒速率", "次/秒", Rate("nitro_buffer_enqueue_failures_total")),
        ]));

        Groups.Add(new MetricGroup("存储",
        [
            new MetricRow("落库失败速率", "次/秒", Rate("nitro_store_write_failures_total")),
            new MetricRow("通道丢弃点速率", "点/秒", Rate("nitro_store_channel_dropped_points_total")),
            new MetricRow("主库大小", "MB", GaugeMb("nitro_db_size_bytes", Label("kind", "main"))),
            new MetricRow("WAL 大小", "MB", GaugeMb("nitro_db_size_bytes", Label("kind", "wal"))),
            new MetricRow("磁盘剩余(最小)", "MB", DiskFreeMb()),
        ]));

        Groups.Add(new MetricGroup("业务",
        [
            new MetricRow("告警触发速率", "次/秒", Rate("nitro_alarm_triggered_total")),
            new MetricRow("告警通知失败速率", "次/秒", Rate("nitro_alarm_notify_failures_total")),
            new MetricRow("配置同步成功速率", "次/秒", Rate("nitro_config_sync_success_total")),
            new MetricRow("配置同步失败速率", "次/秒", Rate("nitro_config_sync_failures_total")),
        ]));
    }

    // ── 计算闭包（每个行持有，刷新时按最新/上一快照求值）──

    private static Func<IReadOnlyDictionary<string, string>, bool> Status(string value)
        => labels => labels.TryGetValue("status", out var s) && s == value;

    private static Func<MetricsSnapshot, MetricsSnapshot?, double, string> Rate(
        string name, Func<IReadOnlyDictionary<string, string>, bool>? filter = null)
        => (cur, prev, elapsed) =>
        {
            if (prev is null || elapsed <= 0)
                return "—";
            var delta = cur.Sum(name, filter) - prev.Sum(name, filter);
            if (delta < 0)
                delta = 0; // Counter 重置（进程重启）当作 0，不显示负速率
            return (delta / elapsed).ToString("N2", Ci);
        };

    private static Func<MetricsSnapshot, MetricsSnapshot?, double, string> Gauge(
        string name, Func<IReadOnlyDictionary<string, string>, bool>? filter = null)
        => (cur, _, _) =>
        {
            var v = cur.Value(name, filter);
            return v is null ? "—" : v.Value.ToString("N0", Ci);
        };

    /// <summary>按标签等值过滤样本。</summary>
    private static Func<IReadOnlyDictionary<string, string>, bool> Label(string key, string value)
        => labels => labels.TryGetValue(key, out var v) && v == value;

    /// <summary>字节值转 MB 显示。</summary>
    private static Func<MetricsSnapshot, MetricsSnapshot?, double, string> GaugeMb(
        string name, Func<IReadOnlyDictionary<string, string>, bool>? filter = null)
        => (cur, _, _) =>
        {
            var v = cur.Value(name, filter);
            return v is null ? "—" : (v.Value / (1024.0 * 1024.0)).ToString("N1", Ci);
        };

    private static Func<MetricsSnapshot, MetricsSnapshot?, double, string> HistP95(string name)
        => (cur, _, _) =>
        {
            var v = cur.HistogramQuantile(name, 0.95);
            return v is null ? "—" : v.Value.ToString("N1", Ci);
        };

    private static Func<MetricsSnapshot, MetricsSnapshot?, double, string> CountOpen()
        => (cur, _, _) => cur.CountValue("nitro_circuit_breaker_state", 1).ToString("N0", Ci);

    private static Func<MetricsSnapshot, MetricsSnapshot?, double, string> MqttState()
        => (cur, _, _) =>
        {
            var v = cur.Value("nitro_mqtt_state");
            if (v is null)
                return "—";
            return (int)v.Value switch
            {
                0 => "未连接",
                1 => "连接中",
                2 => "已连接",
                3 => "重连中",
                4 => "故障",
                _ => "未知"
            };
        };

    private static Func<MetricsSnapshot, MetricsSnapshot?, double, string> DiskFreeMb()
        => (cur, _, _) =>
        {
            var bytes = cur.Min("nitro_disk_free_bytes");
            return bytes is null ? "—" : (bytes.Value / (1024.0 * 1024.0)).ToString("N0", Ci);
        };

    /// <summary>端到端新鲜度 = 当前时间 - 最近样本时间戳；数据停滞时该值只增不减。</summary>
    private static Func<MetricsSnapshot, MetricsSnapshot?, double, string> FreshnessSeconds()
        => (cur, _, _) =>
        {
            var ts = cur.Value("nitro_latest_sample_timestamp_seconds");
            if (ts is null)
                return "—";
            var age = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0 - ts.Value;
            return age < 0 ? "0" : age.ToString("N0", Ci);
        };
}

/// <summary>监控页的一个指标分组（如「采集」「转发」）。</summary>
public sealed class MetricGroup
{
    public MetricGroup(string title, IReadOnlyList<MetricRow> rows)
    {
        Title = title;
        Rows = new ObservableCollection<MetricRow>(rows);
    }

    public string Title { get; }

    public ObservableCollection<MetricRow> Rows { get; }
}

/// <summary>监控页的一行指标：名称 + 单位 + 随时间刷新的显示值。</summary>
public sealed partial class MetricRow : ObservableObject
{
    internal MetricRow(
        string name, string unit,
        Func<MetricsSnapshot, MetricsSnapshot?, double, string> compute)
    {
        Name = name;
        Unit = unit;
        Compute = compute;
    }

    public string Name { get; }

    public string Unit { get; }

    [ObservableProperty] private string _value = "—";

    internal Func<MetricsSnapshot, MetricsSnapshot?, double, string> Compute { get; }
}
