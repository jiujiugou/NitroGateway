using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using Microsoft.Extensions.Logging;
using NitroGateway.Desktop.Messaging;
using NitroGateway.Desktop.Services;
using NitroGateway.DeviceManagement;
using NitroGateway.Domain.Devices;
using NitroGateway.Storage.TimeSeries;
using SkiaSharp;

namespace NitroGateway.Desktop.ViewModels;

/// <summary>
/// 实时数据页：点位值网格（帧驱动）+ 选中点位的实时曲线（LiveCharts2）。
/// 曲线预载近 2 小时历史，之后由帧追加，环形缓冲上限 <see cref="MaxChartPoints"/>。
/// </summary>
public sealed partial class RealtimeViewModel : ObservableObject, IDisposable
{
    /// <summary>曲线环形缓冲上限（1s 采集 ≈ 10 分钟窗口）。</summary>
    private const int MaxChartPoints = 600;

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

    private readonly LineSeries<DateTimePoint> _series;

    public ObservableCollection<DeviceOption> Devices { get; } = [];
    public ObservableCollection<RealtimePointItem> Points { get; } = [];
    public ObservableCollection<DateTimePoint> ChartValues { get; } = [];

    /// <summary>LiveCharts2 绑定：系列 / X 轴（时间）/ Y 轴</summary>
    public ISeries[] Series { get; }
    public Axis[] XAxes { get; }
    public Axis[] YAxes { get; }

    [ObservableProperty] private DeviceOption? _selectedDevice;
    [ObservableProperty] private RealtimePointItem? _selectedPoint;
    [ObservableProperty] private string _statusText = "选择设备查看实时数据";

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

        _series = new LineSeries<DateTimePoint>
        {
            Name = "实时值",
            Fill = null,
            GeometrySize = 0,
            LineSmoothness = 0,
            Stroke = new SolidColorPaint(SKColors.SteelBlue) { StrokeThickness = 1.5f }
        };
        Series = new ISeries[] { _series };
        XAxes = new[] { new Axis { Labeler = value => new DateTime((long)value).ToString("HH:mm:ss"), TextSize = 11 } };
        YAxes = new[] { new Axis { TextSize = 11 } };

        _bridge.FrameReady += OnFrame;
        _ = LoadDevicesAsync();
    }

    partial void OnSelectedDeviceChanged(DeviceOption? value)
    {
        _loadVersion++;
        Points.Clear();
        _pointsById.Clear();
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
        ChartValues.Clear();
        if (value is null)
        {
            _series.Values = null;
            return;
        }
        _series.Values = ChartValues;
        _ = LoadPointHistoryAsync(value.PointId, _loadVersion);
    }

    private async Task LoadDevicesAsync()
    {
        var result = await _cache.GetAllAsync();
        if (result.IsFailure)
        {
            StatusText = $"加载设备失败：{result.Error!.Message}";
            return;
        }

        _ui.Post(() =>
        {
            Devices.Clear();
            foreach (var device in result.Value!)
                Devices.Add(new DeviceOption(device.Id, device.Name));
        });
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
        var latestResult = await _store.QueryLatestAsync(deviceId, pointId: null);
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

        var to = DateTime.UtcNow;
        var result = await _store.QueryPagedAsync(SelectedDevice.Id, pointId, to.AddHours(-2), to, MaxChartPoints, 0);
        if (result.IsFailure)
            return;

        _ui.Post(() =>
        {
            if (version != _loadVersion)
                return; // 过期结果（已切换点位/设备），丢弃
            ChartValues.Clear();
            foreach (var snapshot in result.Value!)
            {
                if (TryToDouble(snapshot.Value, out var value))
                    ChartValues.Add(new DateTimePoint(snapshot.Timestamp.ToLocalTime(), value));
            }
        });
    }

    private void OnFrame(UiFrame frame)
    {
        if (frame.Measurements.Count == 0)
            return;

        _ui.Post(() =>
        {
            foreach (var snapshot in frame.Measurements)
            {
                // 单点数据直刷（D2）
                if (_pointsById.TryGetValue(snapshot.DevicePointId, out var item))
                    item.Update(snapshot);

                // 选中点位追加曲线
                if (SelectedPoint is not null && snapshot.DevicePointId == SelectedPoint.PointId &&
                    TryToDouble(snapshot.Value, out var value))
                {
                    ChartValues.Add(new DateTimePoint(snapshot.Timestamp.ToLocalTime(), value));
                    while (ChartValues.Count > MaxChartPoints)
                        ChartValues.RemoveAt(0);
                }
            }
        });
    }

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

