using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using Microsoft.Extensions.Logging;
using NitroGateway.Desktop.Messaging;
using NitroGateway.Desktop.Services;
using NitroGateway.DeviceManagement;
using NitroGateway.Domain.Devices;
using NitroGateway.Storage.TimeSeries;

namespace NitroGateway.Desktop.ViewModels;

/// <summary>
/// 实时数据页：点位值网格（帧驱动）+ 选中点位的实时曲线（LiveCharts2）。
/// ADR-045：原始数据保留在 <see cref="RawValues"/>（2h/7200 点环形窗口，普通 List 无集合通知），
/// 显示集合 <see cref="ChartValues"/> 由 <see cref="RefreshChart"/> 每 500ms 做 min/max 分桶降采样到
/// <see cref="ChartWindowPoints"/> 内再刷给 LiveCharts（绘制成本 ∝ 点数）；页面不可见
/// （<see cref="IsActive"/>=false，导航切走/窗口最小化）时整帧丢弃并摘除曲线数据。
/// </summary>
public sealed partial class RealtimeViewModel : ObservableObject, IDisposable
{
    /// <summary>
    /// 原始缓冲环形窗口上限（ADR-037 S9：与预载 2 小时历史对齐，1s 采集 ≈ 2 小时窗口）。
    /// 帧更新只追加普通 List，溢出按批 RemoveRange（等价 ADR-037 S12 批量移除），不触发 LiveCharts 通知。
    /// </summary>
    private const int MaxChartPoints = 7200;

    /// <summary>
    /// 显示窗口（降采样后给 LiveCharts 的点数上限，ADR-045 P2）。
    /// min/max 分桶输出 ≤ 该值，重绘成本约为原 7200 点的 1/7。
    /// </summary>
    internal const int ChartWindowPoints = 1000;

    /// <summary>固定刷新节流：最多每 500ms 重绘一次显示集合（ADR-045 P4）。</summary>
    private static readonly TimeSpan ChartRefreshInterval = TimeSpan.FromMilliseconds(500);

    private readonly IDeviceSnapshotCache _cache;
    private readonly IMeasurementStore _store;
    private readonly UiDispatcher _ui;
    private readonly EventBridge _bridge;
    private readonly ILogger<RealtimeViewModel> _logger;
    private readonly Dictionary<Guid, RealtimePointItem> _pointsById = [];

    /// <summary>
    /// 加载版本号：设备/点位切换时递增，UI 回调校验版本一致才应用，
    /// 过期结果（旧设备/旧点位晚到）直接丢弃（ADR-027 P1-1）。
    /// </summary>
    private int _loadVersion;

    /// <summary>上次降采样刷新时刻（UTC），用于固定刷新节流（ADR-045 P4）。</summary>
    private DateTime _lastChartRefreshUtc = DateTime.UtcNow;

    /// <summary>
    /// 原始缓冲（ADR-045 P2）：选中点位全部原始点，非 UI 绑定、无集合通知 → 帧追加零重绘。
    /// </summary>
    private readonly List<DateTimePoint> _rawValues = new(MaxChartPoints);

    private readonly LineSeries<DateTimePoint> _series;

    public ObservableCollection<DeviceOption> Devices { get; } = [];
    public ObservableCollection<RealtimePointItem> Points { get; } = [];

    /// <summary>显示集合（LiveCharts2 绑定）：<see cref="RefreshChart"/> 降采样后的窗口。</summary>
    public RingObservableCollection<DateTimePoint> ChartValues { get; } = [];

    /// <summary>原始缓冲只读视图（测试可见）。</summary>
    internal IReadOnlyList<DateTimePoint> RawValues => _rawValues;

    /// <summary>LiveCharts2 绑定：系列 / X 轴（时间）/ Y 轴</summary>
    public ISeries[] Series { get; }
    public Axis[] XAxes { get; }
    public Axis[] YAxes { get; }

    [ObservableProperty] private DeviceOption? _selectedDevice;
    [ObservableProperty] private RealtimePointItem? _selectedPoint;
    [ObservableProperty] private string _statusText = "选择设备查看实时数据";

    /// <summary>
    /// 页面是否活跃（实时页在前台且窗口未最小化，ADR-045 P1）。
    /// 由 MainViewModel 在导航切换 / 窗口最小化时置位；false 时 OnFrame 整帧丢弃并摘除曲线数据。
    /// </summary>
    [ObservableProperty] private bool _isActive = true;

    public RealtimeViewModel(
        IDeviceSnapshotCache cache,
        IMeasurementStore store,
        UiDispatcher ui,
        EventBridge bridge,
        ILogger<RealtimeViewModel> logger)
    {
        _cache = cache;
        _store = store;
        _ui = ui;
        _bridge = bridge;
        _logger = logger;

        // 图表渲染细节（配色/坐标轴/labeler）集中在 RealtimeChartFactory，
        // ViewModel 只持有绑定对象，读代码不再穿过表现层噪音（ADR-045）。
        _series = RealtimeChartFactory.CreateSeries();
        Series = new ISeries[] { _series };
        var axes = RealtimeChartFactory.CreateAxes();
        XAxes = new[] { axes.X };
        YAxes = new[] { axes.Y };

        _bridge.FrameReady += OnFrame;
        _ = LoadDevicesAsync();
    }

    partial void OnSelectedDeviceChanged(DeviceOption? value)
    {
        _loadVersion++;
        Points.Clear();
        _pointsById.Clear();
        _rawValues.Clear();
        ChartValues.Clear();
        _series.Values = null;
        SelectedPoint = null;

        if (value is null)
        {
            StatusText = "选择设备查看实时数据";
            return;
        }
        _ = LoadPointsAsync(value.Id, _loadVersion);
    }

    partial void OnSelectedPointChanged(RealtimePointItem? value)
    {
        _loadVersion++;
        _rawValues.Clear();
        ChartValues.Clear();
        if (value is null)
        {
            _series.Values = null;
            return;
        }
        _series.Values = ChartValues;
        _ = LoadPointHistoryAsync(value.PointId, _loadVersion);
    }

    /// <summary>
    /// 页面可见性切换（ADR-045 P1）：
    /// 恢复可见 → 重载选中点位最近 2h 窗口；离开/最小化 → 丢弃在途加载并摘除曲线数据，
    /// LiveCharts 无数据可持有，顺带缓解切页资源不回收（LiveCharts2 #1468）。
    /// </summary>
    partial void OnIsActiveChanged(bool value)
    {
        if (value)
        {
            // ADR-048：重新进入实时页时重载设备下拉（设备页新增/编辑/删除后设备目录缓存已
            // Invalidate），保证新增设备能出现在下拉列表；增量对账不清空重建，选中不丢失。
            _ = LoadDevicesAsync();
            if (SelectedPoint is not null)
                _ = LoadPointHistoryAsync(SelectedPoint.PointId, _loadVersion);
        }
        else
        {
            _loadVersion++;
            _rawValues.Clear();
            ChartValues.Clear();
            _series.Values = null;
        }
    }

    /// <summary>
    /// 加载设备下拉列表（构造时与重新进入实时页时调用，ADR-048）。
    /// 设备目录缓存（<see cref="IDeviceSnapshotCache"/>）在配置写入后已 Invalidate，
    /// 本方法随时重取即拿到最新目录；对 <see cref="Devices"/> 做增量对账——
    /// 删除已不存在的设备、新增设备追加、重命名替换对应项，不清空重建以免打断 ComboBox 选中。
    /// </summary>
    private async Task LoadDevicesAsync()
    {
        var result = await _cache.GetAllAsync();
        if (result.IsFailure)
        {
            StatusText = $"加载设备失败：{result.Error!.Message}";
            return;
        }

        var latest = result.Value!;
        _ui.Post(() => ApplyDeviceDiff(latest));
    }

    /// <summary>
    /// 把最新设备目录增量应用到 <see cref="Devices"/>（ADR-048）：
    /// ① 移除已不存在的设备；② 新增设备追加到末尾，重命名设备替换对应项；
    /// ③ 选中设备仍存在则按 Id 重指向最新项（重命名后旧实例已不在集合，ComboBox 会丢显示），
    /// 选中设备被删除则清空选中。不清空重建，避免 ComboBox 选中丢失。
    /// </summary>
    private void ApplyDeviceDiff(IReadOnlyList<Device> latest)
    {
        var selectedId = SelectedDevice?.Id;

        // ① 移除已不存在的设备（倒序 RemoveAt，保留现有顺序）
        var latestIds = latest.Select(d => d.Id).ToHashSet();
        for (var i = Devices.Count - 1; i >= 0; i--)
        {
            if (!latestIds.Contains(Devices[i].Id))
                Devices.RemoveAt(i);
        }

        // ② 新增设备追加到末尾；重命名设备替换对应项（DeviceOption 是记录，需换新实例）
        var existingById = Devices.ToDictionary(o => o.Id);
        foreach (var device in latest)
        {
            if (!existingById.TryGetValue(device.Id, out var option))
            {
                Devices.Add(new DeviceOption(device.Id, device.Name));
            }
            else if (!string.Equals(option.Name, device.Name, StringComparison.Ordinal))
            {
                Devices[Devices.IndexOf(option)] = new DeviceOption(device.Id, device.Name);
            }
        }

        // ③ 恢复选中：选中设备仍存在则按 Id 重指向最新项；被删除则清空选中。
        // 仅在实例变化（重命名替换）时重设，未变化则不动，避免无谓重载点位。
        if (selectedId is Guid sid)
        {
            var fresh = Devices.FirstOrDefault(o => o.Id == sid);
            if (fresh is not null && !ReferenceEquals(fresh, SelectedDevice))
                SelectedDevice = fresh;
            else if (fresh is null)
                SelectedDevice = null;
        }
    }

    private async Task LoadPointsAsync(Guid deviceId, int version)
    {
        var result = await _cache.GetAllAsync();
        if (result.IsFailure)
        {
            StatusText = $"加载点位失败：{result.Error!.Message}";
            return;
        }

        var device = result.Value!.FirstOrDefault(d => d.Id == deviceId);
        if (device is null)
            return;

        // 首屏：每点最新值（SQL 直接取最新，ADR-002 P2-4）
        // ADR-047：Microsoft.Data.Sqlite 的 async 实为「同步外包」（QueryAsync 在调用线程同步跑完才返回
        // 已完成 Task；连接串 Asynchronous 关键字已在 10.x 移除，无法从连接串侧真异步），
        // 这里包 Task.Run 把查询移出 UI 线程，避免切设备时扫全设备历史冻结窗口。
        var latestResult = await Task.Run(() => _store.QueryLatestAsync(deviceId, pointId: null));
        var latestByPoint = latestResult.IsSuccess
            ? latestResult.Value!
                .GroupBy(s => s.DevicePointId)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(s => s.Timestamp).First())
            : new Dictionary<Guid, PointSnapshot>();

        _ui.Post(() =>
        {
            if (version != _loadVersion)
                return; // 过期结果（已切换设备），丢弃
            Points.Clear();
            _pointsById.Clear();
            foreach (var point in device.Points.Where(p => p.Enabled))
            {
                var item = new RealtimePointItem
                {
                    PointId = point.Id,
                    Name = point.Name,
                    Address = point.Address,
                    DataType = point.DataType.ToString()
                };
                if (latestByPoint.TryGetValue(point.Id, out var snapshot))
                    item.Update(snapshot);
                Points.Add(item);
                _pointsById[point.Id] = item;
            }
            StatusText = $"设备「{device.Name}」共 {Points.Count} 个点位";
        });
    }

    private async Task LoadPointHistoryAsync(Guid pointId, int version)
    {
        if (SelectedDevice is null)
            return;

        // ADR-047：先捕获 deviceId 到局部变量（异步期间不依赖可变属性，防止 await 期间被切走），
        // 再包 Task.Run 把查询移出 UI 线程（SQLite async 是同步外包，否则切回实时页/切点位冻结窗口）。
        var deviceId = SelectedDevice.Id;
        var to = DateTime.UtcNow;
        var result = await Task.Run(() => _store.QueryPagedAsync(deviceId, pointId, to.AddHours(-2), to, MaxChartPoints, 0));
        if (result.IsFailure)
            return;

        _ui.Post(() =>
        {
            if (version != _loadVersion)
                return; // 过期结果（已切换点位/设备），丢弃
            _rawValues.Clear();
            foreach (var snapshot in result.Value!)
            {
                if (TryToDouble(snapshot.Value, out var value))
                    _rawValues.Add(new DateTimePoint(snapshot.Timestamp.ToLocalTime(), value));
            }
            RefreshChart(); // 历史立即画出，不等 500ms 节流
        });
    }

    private void OnFrame(UiFrame frame)
    {
        // ADR-045 P1：页面不可见（切走/最小化）时整帧丢弃，不追加、不重绘
        if (!IsActive || frame.Measurements.Count == 0)
            return;

        _ui.Post(() =>
        {
            var appendChart = false;
            foreach (var snapshot in frame.Measurements)
            {
                // 单点数据直刷（D2）
                if (_pointsById.TryGetValue(snapshot.DevicePointId, out var item))
                    item.Update(snapshot);

                // 选中点位追加原始缓冲（普通 List 无通知 → 帧级零重绘，ADR-045 P2）
                if (SelectedPoint is not null && snapshot.DevicePointId == SelectedPoint.PointId &&
                    TryToDouble(snapshot.Value, out var value))
                {
                    _rawValues.Add(new DateTimePoint(snapshot.Timestamp.ToLocalTime(), value));
                    appendChart = true;
                    var overflow = _rawValues.Count - MaxChartPoints;
                    if (overflow > 0)
                        _rawValues.RemoveRange(0, overflow); // 批量裁剪（等价 ADR-037 S12）
                }
            }

            // ADR-045 P4：固定刷新——最多每 ChartRefreshInterval 重绘一次（500ms），
            // 由 RefreshChart 降采样后单次 Reset 刷给 LiveCharts
            if (appendChart && DateTime.UtcNow - _lastChartRefreshUtc >= ChartRefreshInterval)
            {
                _lastChartRefreshUtc = DateTime.UtcNow;
                RefreshChart();
            }
        });
    }

    /// <summary>
    /// 把原始缓冲降采样刷到显示集合（ADR-045 P2/P4）。仅活跃且有选中点位时执行。
    /// </summary>
    internal void RefreshChart()
    {
        if (!IsActive || SelectedPoint is null)
            return;
        var sampled = DownsampleMinMax(_rawValues, ChartWindowPoints);
        ChartValues.Replace(sampled);
        _series.Values = ChartValues;
    }

    /// <summary>
    /// min/max 分桶降采样（ADR-045 P2）：按时间把原始点均匀分到 target/2 个桶，
    /// 每桶保留最小值和最大值两个点，保尖峰/谷底形状（比隔点抽稀更保真），输出 ≤ target 点；
    /// 保证首点与最新点始终在（实时曲线右边缘 = 最新值）。
    /// </summary>
    internal static List<DateTimePoint> DownsampleMinMax(IReadOnlyList<DateTimePoint> source, int target)
    {
        var n = source.Count;
        if (n <= target || target < 2)
            return [.. source];

        var buckets = Math.Max(2, target / 2);
        var min = new DateTimePoint?[buckets];
        var max = new DateTimePoint?[buckets];
        var seen = new bool[buckets];

        var t0 = source[0].DateTime.Ticks;
        var span = source[^1].DateTime.Ticks - t0;

        for (var i = 0; i < n; i++)
        {
            var p = source[i];
            if (!p.Value.HasValue)
                continue; // 无值点不参与采样
            var value = p.Value.Value;
            var bucket = span <= 0
                ? 0
                : (int)Math.Min(buckets - 1, Math.Max(0, (p.DateTime.Ticks - t0) * (long)buckets / span));
            if (!seen[bucket])
            {
                min[bucket] = p;
                max[bucket] = p;
                seen[bucket] = true;
            }
            else
            {
                if (value < min[bucket]!.Value!.Value) min[bucket] = p;
                if (value > max[bucket]!.Value!.Value) max[bucket] = p;
            }
        }

        var result = new List<DateTimePoint>(Math.Min(n, target + 1));
        for (var b = 0; b < buckets; b++)
        {
            if (!seen[b])
                continue;
            var lo = min[b]!;
            var hi = max[b]!;
            if (result.Count == 0 || !SamePoint(result[^1], lo))
                result.Add(lo);
            if (!SamePoint(lo, hi))
                result.Add(hi);
            if (result.Count >= target)
                break; // 保险上限
        }

        // 最新一点始终保留（右边缘）
        var last = source[^1];
        if (result.Count < target && (result.Count == 0 || !SamePoint(result[^1], last)))
            result.Add(last);
        return result;
    }

    private static bool SamePoint(DateTimePoint a, DateTimePoint b)
        => a.DateTime == b.DateTime && a.Value == b.Value;

    /// <summary>尝试把点位值转 double（Bool/Int/Float/Double/String 均可转换时参与曲线）。</summary>
    private static bool TryToDouble(object? value, out double result)
    {
        result = 0;
        if (value is null)
            return false;
        try
        {
            if (value is IConvertible convertible)
            {
                result = convertible.ToDouble(CultureInfo.InvariantCulture);
                return !double.IsNaN(result) && !double.IsInfinity(result);
            }
        }
        catch
        {
            // 非数值点位（如字符串）不上曲线
        }
        return false;
    }

    public void Dispose()
    {
        _bridge.FrameReady -= OnFrame;
    }
}

/// <summary>实时点位行（值/质量/时间随帧刷新，ObservableObject 供 DataGrid 绑定）</summary>
public sealed partial class RealtimePointItem : ObservableObject
{
    public Guid PointId { get; init; }
    public required string Name { get; init; }
    public required string Address { get; init; }
    public required string DataType { get; init; }

    [ObservableProperty] private string _valueText = "—";
    [ObservableProperty] private string _qualityText = "—";
    [ObservableProperty] private string _timestampText = "—";
    [ObservableProperty] private bool _isBad;

    /// <summary>用一帧快照刷新本行显示值。</summary>
    public void Update(PointSnapshot snapshot)
    {
        ValueText = snapshot.Value?.ToString() ?? "—";
        QualityText = snapshot.Quality == QualityCode.Good ? "Good" : snapshot.Quality.ToString();
        TimestampText = snapshot.Timestamp.ToLocalTime().ToString("HH:mm:ss");
        IsBad = snapshot.Quality != QualityCode.Good;
    }
}
