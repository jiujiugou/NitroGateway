using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using NitroGateway.Desktop.Services.Infrastructure;

using NitroGateway.DeviceManagement;
using NitroGateway.Domain.Devices;
using NitroGateway.Storage.TimeSeries;

namespace NitroGateway.Desktop.ViewModels;

/// <summary>
/// 历史查询页：按 设备 + 点位 + 日期区间 分页查询时序库（QueryPagedAsync，ADR-005 P2-2）。
/// 查询窗口为 [FromDate 00:00, ToDate 次日 00:00)（本地时区）。
/// </summary>
public sealed partial class HistoryViewModel : ObservableObject
{
    /// <summary>单页条数（与 QueryPagedAsync 上限一致，翻页用 offset 递增）。</summary>
    private const int PageSize = 1000;

    /// <summary>
    /// 加载版本号：设备/点位切换或发起新查询时递增，
    /// UI 回调校验版本一致才应用，过期结果直接丢弃（ADR-027 P2-1）。
    /// </summary>
    private int _loadVersion;

    private readonly IDeviceSnapshotCache _cache;
    private readonly IMeasurementStore _store;
    private readonly UiDispatcher _ui;
    private readonly ILogger<HistoryViewModel> _logger;

    public ObservableCollection<DeviceOption> Devices { get; } = [];
    public ObservableCollection<PointOption> Points { get; } = [];
    public ObservableCollection<HistoryRow> Rows { get; } = [];

    [ObservableProperty] private DeviceOption? _selectedDevice;
    [ObservableProperty] private PointOption? _selectedPoint;

    [ObservableProperty] private DateTime? _fromDate = DateTime.Today.AddDays(-1);
    [ObservableProperty] private DateTime? _toDate = DateTime.Today;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyPropertyChangedFor(nameof(CanGoPrev))]
    [NotifyPropertyChangedFor(nameof(CanGoNext))]
    private bool _isLoading;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoPrev))]
    private int _pageNumber = 1;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoNext))]
    private bool _hasMore;

    /// <summary>加载完成标志（ADR-037 S3）：查询中禁用查询/翻页按钮。</summary>
    public bool IsIdle => !IsLoading;

    /// <summary>上一页可用：非首页且不在查询中。</summary>
    public bool CanGoPrev => PageNumber > 1 && IsIdle;

    /// <summary>下一页可用：当前页取满一页（可能还有更多）且不在查询中。</summary>
    public bool CanGoNext => HasMore && IsIdle;

    public HistoryViewModel(
        IDeviceSnapshotCache cache,
        IMeasurementStore store,
        UiDispatcher ui,
        ILogger<HistoryViewModel> logger)
    {
        _cache = cache;
        _store = store;
        _ui = ui;
        _logger = logger;
        _ = LoadDevicesAsync();
    }

    partial void OnSelectedDeviceChanged(DeviceOption? value)
    {
        _loadVersion++;
        Points.Clear();
        SelectedPoint = null;
        if (value is null)
            return;
        _ = LoadPointsAsync(value.Id, _loadVersion);
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

        _ui.Post(() =>
        {
            if (version != _loadVersion)
                return; // 过期结果（已切换设备），丢弃
            Points.Clear();
            foreach (var point in device.Points.Where(p => p.Enabled))
                Points.Add(new PointOption(point.Id, point.Name, point.Address));
        });
    }

    /// <summary>快捷区间：1/3/7 天（含今天，日期级窗口）。</summary>
    [RelayCommand]
    private void QuickRange(string days)
    {
        var n = int.Parse(days, System.Globalization.CultureInfo.InvariantCulture);
        ToDate = DateTime.Today;
        FromDate = DateTime.Today.AddDays(-(n - 1));
        _ = QueryAsync();
    }

    /// <summary>查询（回到第 1 页）。</summary>
    [RelayCommand]
    private Task QueryAsync() => QueryPageAsync(page: 1);

    /// <summary>上一页。</summary>
    [RelayCommand]
    private Task PrevPageAsync() => QueryPageAsync(PageNumber - 1);

    /// <summary>下一页。</summary>
    [RelayCommand]
    private Task NextPageAsync() => QueryPageAsync(PageNumber + 1);

    private async Task QueryPageAsync(int page)
    {
        if (IsLoading)
            return; // 防重入：上一查询仍在途

        if (SelectedDevice is null || SelectedPoint is null)
        {
            StatusText = "请先选择设备与点位";
            return;
        }

        if (FromDate is null || ToDate is null)
        {
            StatusText = "请选择起止日期";
            return;
        }

        var version = ++_loadVersion;
        IsLoading = true;
        try
        {
            // 日期级窗口：从 FromDate 00:00（本地）到 ToDate 次日 00:00（本地），转 UTC 查询
            var fromUtc = FromDate.Value.Date.ToUniversalTime();
            var toUtc = ToDate.Value.Date.AddDays(1).ToUniversalTime();

            // ADR-047：先捕获 deviceId/pointId 到局部变量（异步期间不依赖可变属性），
            // 再包 Task.Run 把查询移出 UI 线程（SQLite async 是同步外包，否则历史查询点击冻结窗口）。
            var deviceId = SelectedDevice.Id;
            var pointId = SelectedPoint.Id;
            var result = await Task.Run(() => _store.QueryPagedAsync(
                deviceId, pointId, fromUtc, toUtc, PageSize, (page - 1) * PageSize));

            if (result.IsFailure)
            {
                StatusText = $"查询失败：{result.Error!.Message}";
                return;
            }

            _ui.Post(() =>
            {
                if (version != _loadVersion)
                    return; // 查询期间用户已切换设备/点位，丢弃过期结果
                PageNumber = page;
                Rows.Clear();
                foreach (var snapshot in result.Value!)
                {
                    Rows.Add(new HistoryRow
                    {
                        Timestamp = snapshot.Timestamp,
                        ValueText = snapshot.Value?.ToString() ?? "—",
                        RawValueText = snapshot.RawValue?.ToString() ?? "—",
                        Quality = snapshot.Quality == QualityCode.Good ? "Good" : snapshot.Quality.ToString(),
                        Error = snapshot.ErrorMessage
                    });
                }
                HasMore = result.Value!.Count == PageSize;
                StatusText = $"第 {page} 页，共 {Rows.Count} 条记录" + (HasMore ? "（还有更多）" : "");
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "历史查询失败");
            StatusText = "历史查询失败";
        }
        finally
        {
            IsLoading = false;
        }
    }
}

/// <summary>历史查询结果行</summary>
public sealed class HistoryRow
{
    public DateTime Timestamp { get; init; }
    public required string ValueText { get; init; }
    public required string RawValueText { get; init; }
    public required string Quality { get; init; }
    public string? Error { get; init; }

    public string TimestampText => Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff");
}
