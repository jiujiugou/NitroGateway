using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using Microsoft.Extensions.Logging;
using NitroGateway.Desktop.Messaging;
using NitroGateway.Desktop.Services.Infrastructure;
using NitroGateway.DeviceManagement;
using NitroGateway.Domain.Devices;
using NitroGateway.Storage.TimeSeries;

namespace NitroGateway.Desktop.ViewModels;

public sealed partial class RealtimeViewModel : ObservableObject, IDisposable
{
    private const int MaxChartPoints = 7200;

    internal const int ChartWindowPoints = 1000;

    private static readonly TimeSpan ChartRefreshInterval = TimeSpan.FromMilliseconds(500);

    internal TimeSpan GridRefreshInterval { get; set; } = TimeSpan.FromMilliseconds(500);

    private readonly IDeviceSnapshotCache _cache;
    private readonly IMeasurementStore _store;
    private readonly UiDispatcher _ui;
    private readonly EventBridge _bridge;
    private readonly ILogger<RealtimeViewModel> _logger;
    private readonly IWriteService? _writeService;
    private readonly Dictionary<Guid, RealtimePointItem> _pointsById = [];

    private readonly Dictionary<Guid, PointSnapshot> _latestByPoint = [];

    private int _loadVersion;

    private DateTime _lastChartRefreshUtc = DateTime.UtcNow;

    private DateTime _lastGridRefreshUtc = DateTime.UtcNow;

    private readonly List<DateTimePoint> _rawValues = new(MaxChartPoints);

    private readonly LineSeries<DateTimePoint> _series;

    public ObservableCollection<DeviceOption> Devices { get; } = [];

    public RingObservableCollection<RealtimePointItem> Points { get; } = [];

    /// <summary>显示集合（LiveCharts2 绑定）：<see cref="RefreshChart"/> 降采样后的窗口。</summary>
    public RingObservableCollection<DateTimePoint> ChartValues { get; } = [];

    /// <summary>原始缓冲只读视图（测试可见）。</summary>
    internal IReadOnlyList<DateTimePoint> RawValues => _rawValues;

    internal IReadOnlyDictionary<Guid, PointSnapshot> LatestByPoint => _latestByPoint;

    /// <summary>LiveCharts2 绑定：系列 / X 轴（时间）/ Y 轴</summary>
    public ISeries[] Series { get; }
    public Axis[] XAxes { get; }
    public Axis[] YAxes { get; }

    [ObservableProperty] private DeviceOption? _selectedDevice;
    [ObservableProperty] private RealtimePointItem? _selectedPoint;
    [ObservableProperty] private string _statusText = "选择设备查看实时数据";

    [ObservableProperty] private bool _isActive = true;

    /// <summary>行内写值编辑器（选中可写点位后弹出就地输入，借鉴 ThingsGateway 行内交互）。</summary>
    [ObservableProperty] private WriteValueEditor? _currentWriteEditor;

    /// <summary>行内写值编辑器是否展开（true 时曲线卡片头部就地显示输入区）。</summary>
    [ObservableProperty] private bool _isWriteEditorOpen;

    /// <summary>写值请求执行中（禁用确认/取消，防重复下发）。</summary>
    [ObservableProperty] private bool _isWriting;

    public RealtimeViewModel(
        IDeviceSnapshotCache cache,
        IMeasurementStore store,
        UiDispatcher ui,
        EventBridge bridge,
        ILogger<RealtimeViewModel> logger,
        IWriteService? writeService = null)
    {
        _cache = cache;
        _store = store;
        _ui = ui;
        _bridge = bridge;
        _logger = logger;
        _writeService = writeService;

        // 图表渲染细节（配色/坐标轴/labeler）集中在 RealtimeChartFactory，
        _series = RealtimeChartFactory.CreateSeries();
        Series = new ISeries[] { _series };
        var axes = RealtimeChartFactory.CreateAxes();
        XAxes = new[] { axes.X };
        YAxes = new[] { axes.Y };

        _bridge.FrameReady += OnFrame;
        _ = LoadDevicesAsync();
    }

    /// <summary>
    /// 写值入口命令：曲线卡片头“✎ 写值”按钮在选中可写点位时显示。
    /// 不再弹独立窗口（<see cref="WriteValueWindow"/>），改为行内就地编辑器
    /// （借鉴 ThingsGateway 行内交互）：填充 <see cref="CurrentWriteEditor"/> 并展开
    /// <see cref="IsWriteEditorOpen"/>，用户确认后由 <see cref="ConfirmWriteAsync"/> 走
    /// IWriteService 统一链路（Access + WriteGuard 三级门控 → 驱动写）。
    /// </summary>
    [RelayCommand]
    private void Write(RealtimePointItem? point)
    {
        if (point is null || !point.CanWrite)
            return;
        var device = SelectedDevice;
        if (device is null)
            return;

        CurrentWriteEditor = new WriteValueEditor
        {
            DeviceId = device.Id,
            PointId = point.PointId,
            DeviceName = device.Name,
            PointName = point.Name,
            Address = point.Address,
            DataType = point.DataType,
            CurrentValueText = point.ValueText,
            // 预填当前值：Bool 点位默认按当前值勾选（与 Web 端预填一致），数值/字符串由用户输入。
            BoolValue = point.DataType == "Bool" && (point.ValueText is "1" or "True" or "true"),
            // 桌面行模型未透传 MinLimit/MaxLimit，先显示“不限”（服务侧 WriteGuard 仍按配置校验）
            RangeText = "不限"
        };
        IsWriteEditorOpen = true;
    }

    /// <summary>
    /// 确认下发：把行内编辑器值按 DataType 形态（Bool → bool / 其余 → 字符串）封装
    /// <see cref="WriteRequest"/>，调 <see cref="IWriteService.WriteAsync"/>（与 Web 写端点同一链路）。
    /// 成功关闭编辑器并在状态栏提示；失败保留编辑器并把原因写进 <see cref="StatusText"/>。
    /// </summary>
    [RelayCommand]
    private async Task ConfirmWriteAsync()
    {
        var editor = CurrentWriteEditor;
        if (editor is null || _writeService is null)
        {
            StatusText = _writeService is null ? "写值服务不可用" : "";
            return;
        }

        IsWriting = true;
        try
        {
            var request = new WriteRequest
            {
                DeviceId = editor.DeviceId,
                PointId = editor.PointId,
                Value = editor.IsBool ? editor.BoolValue : (object)editor.InputValue
            };
            var result = await _writeService.WriteAsync(request);
            if (result.IsSuccess)
            {
                var displayValue = editor.IsBool ? (editor.BoolValue ? "1" : "0") : editor.InputValue;
                StatusText = $"写值成功：{editor.PointName} → {displayValue}";
                IsWriteEditorOpen = false;
                CurrentWriteEditor = null;
            }
            else
            {
                StatusText = $"写值失败：{result.Error!.Message}";
            }
        }
        finally
        {
            IsWriting = false;
        }
    }

    /// <summary>取消行内写值：关闭编辑器并丢弃输入。</summary>
    [RelayCommand]
    private void CancelWrite()
    {
        IsWriteEditorOpen = false;
        CurrentWriteEditor = null;
    }

    partial void OnSelectedDeviceChanged(DeviceOption? value)
    {
        _loadVersion++;
        _rawValues.Clear();
        ChartValues.Clear();
        _series.Values = null;
        SelectedPoint = null;

        if (value is null)
        {
            // 用 Points.Replace 一次 Reset 重建（避免 Clear + Replace 两次整表通知，减少切换卡顿感）。
            Points.Clear();
            _pointsById.Clear();
            StatusText = "选择设备查看实时数据";
            return;
        }
        _ = LoadPointsAsync(value.Id, _loadVersion);
    }

    partial void OnSelectedPointChanged(RealtimePointItem? value)
    {
        // 写值编辑器与选中点位强绑定：切换点位立即关闭编辑器，防止把新值误下发给旧点位（控制动作安全）。
        IsWriteEditorOpen = false;
        CurrentWriteEditor = null;

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

    partial void OnIsActiveChanged(bool value)
    {
        if (value)
        {
            // Invalidate），保证新增设备能出现在下拉列表；增量对账不清空重建，选中不丢失。
            _ = LoadDevicesAsync();
            if (SelectedPoint is not null)
                _ = LoadPointHistoryAsync(SelectedPoint.PointId, _loadVersion);
            // 失焦期间帧已丢弃，但边界帧可能已入缓存未刷表；恢复即刷，表格立即为最新值。
            RefreshGridFromCache();
        }
        else
        {
            _loadVersion++;
            _rawValues.Clear();
            ChartValues.Clear();
            _series.Values = null;
        }
    }

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

        var enabled = device.Points.Where(p => p.Enabled).ToList();

        //    未在帧中出现过的点位（冷启动/离线）先显示「—」，由步骤 ② 或后续帧补齐。
        _ui.Post(() =>
        {
            if (version != _loadVersion)
                return; // 过期结果（已切换设备），丢弃

            var items = new List<RealtimePointItem>(enabled.Count);
            _pointsById.Clear(); // 与 Points 同步重建；切设备后旧点位字典在此一并清掉
            foreach (var point in enabled)
            {
                var item = new RealtimePointItem
                {
                    PointId = point.Id,
                    Name = point.Name,
                    Address = point.Address,
                    DataType = point.DataType.ToString(),
                    Access = point.Access
                };
                if (_latestByPoint.TryGetValue(point.Id, out var snapshot))
                    item.Update(snapshot);
                items.Add(item);
                _pointsById[point.Id] = item;
            }
            Points.Replace(items); // 单次 Reset 重建，替代逐条 Add 的 N 次通知（大点位设备切换更快）
            StatusText = $"设备「{device.Name}」共 {Points.Count} 个点位";
        });

        //    结果只填充仍缺失的点位——帧数据更新鲜，以帧为准、不覆盖。
        var missing = enabled.Where(p => !_latestByPoint.ContainsKey(p.Id)).ToList();
        if (missing.Count == 0)
            return;

        // 已完成 Task；连接串 Asynchronous 关键字已在 10.x 移除，无法从连接串侧真异步），
        // 这里包 Task.Run 把查询移出 UI 线程，避免切设备时扫全设备历史冻结窗口。
        var latestResult = await Task.Run(() => _store.QueryLatestAsync(deviceId, pointId: null));
        if (latestResult.IsFailure)
            return;

        _ui.Post(() =>
        {
            if (version != _loadVersion)
                return; // 过期结果（已切换设备/点位），丢弃
            foreach (var snapshot in latestResult.Value!)
            {
                if (_latestByPoint.ContainsKey(snapshot.DevicePointId))
                    continue; // 帧内存已更新（更新鲜），以帧为准、不覆盖
                _latestByPoint[snapshot.DevicePointId] = snapshot;
                if (_pointsById.TryGetValue(snapshot.DevicePointId, out var item))
                    item.Update(snapshot);
            }
        });
    }

    private async Task LoadPointHistoryAsync(Guid pointId, int version)
    {
        if (SelectedDevice is null)
            return;

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
        if (!IsActive || frame.Measurements.Count == 0)
            return;

        _ui.Post(() =>
        {
            var appendChart = false;
            foreach (var snapshot in frame.Measurements)
            {
                _latestByPoint[snapshot.DevicePointId] = snapshot;

                // 值已入内存缓存（O(1) 无通知），DataGrid 行由下方节流批量刷

                if (SelectedPoint is not null && snapshot.DevicePointId == SelectedPoint.PointId &&
                    TryToDouble(snapshot.Value, out var value))
                {
                    _rawValues.Add(new DateTimePoint(snapshot.Timestamp.ToLocalTime(), value));
                    appendChart = true;
                    var overflow = _rawValues.Count - MaxChartPoints;
                    if (overflow > 0)
                        _rawValues.RemoveRange(0, overflow);
                }
            }

            if (DateTime.UtcNow - _lastGridRefreshUtc >= GridRefreshInterval)
            {
                _lastGridRefreshUtc = DateTime.UtcNow;
                RefreshGridFromCache();
            }

            // 由 RefreshChart 降采样后单次 Reset 刷给 LiveCharts
            if (appendChart && DateTime.UtcNow - _lastChartRefreshUtc >= ChartRefreshInterval)
            {
                _lastChartRefreshUtc = DateTime.UtcNow;
                RefreshChart();
            }
        });
    }

    internal void RefreshGridFromCache()
    {
        if (_pointsById.Count == 0)
            return;
        foreach (var (pointId, item) in _pointsById)
        {
            if (_latestByPoint.TryGetValue(pointId, out var snapshot))
                item.Update(snapshot);
        }
    }

    internal void RefreshChart()
    {
        if (!IsActive || SelectedPoint is null)
            return;
        var sampled = DownsampleMinMax(_rawValues, ChartWindowPoints);
        ChartValues.Replace(sampled);
        _series.Values = ChartValues;
    }

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
    public required PointAccess Access { get; init; }

    /// <summary>是否可写（Access ∈ {WriteOnly, ReadWrite}），供“选中点位”上下文写值按钮显隐。</summary>
    public bool CanWrite => Access is PointAccess.WriteOnly or PointAccess.ReadWrite;

    [ObservableProperty] private string _valueText = "—";
    [ObservableProperty] private string _qualityText = "—";
    [ObservableProperty] private string _timestampText = "—";
    [ObservableProperty] private bool _isBad;

    /// <summary>用一帧快照刷新本行显示值。</summary>
    public void Update(PointSnapshot snapshot)
    {
        ValueText = snapshot.Value?.ToString() ?? "—";
        QualityText = snapshot.Quality == QualityCode.Good ? "Good" : snapshot.Quality.ToString();
        TimestampText = snapshot.Timestamp.ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.CurrentCulture);
        IsBad = snapshot.Quality != QualityCode.Good;
    }
}
