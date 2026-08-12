using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NitroGateway.Desktop.Services;
using NitroGateway.DeviceManagement;
using NitroGateway.Domain.Devices;
using NitroGateway.Shared;

namespace NitroGateway.Desktop.ViewModels;

/// <summary>
/// 设备点位管理（ADR-029 P2）：打开点位窗口后加载该设备点位列表，
/// 增/删/改走 IPointManager（Scoped，命令内建作用域解析）。
/// </summary>
public sealed partial class PointsViewModel : ObservableObject, IDisposable
{
    private readonly Guid _deviceId;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDeviceDialogService _dialogs;
    private readonly IConfigSyncOutboxStore _outbox;
    private readonly ILogger<PointsViewModel> _logger;

    public ObservableCollection<PointItem> Items { get; } = [];

    /// <summary>设备名称（窗口标题用）</summary>
    public string DeviceName { get; }

    [ObservableProperty] private PointItem? _selectedPoint;
    [ObservableProperty] private string _statusText = "";

    public PointsViewModel(
        Guid deviceId,
        string deviceName,
        IServiceScopeFactory scopeFactory,
        IDeviceDialogService dialogs,
        IConfigSyncOutboxStore outbox,
        ILogger<PointsViewModel> logger)
    {
        _deviceId = deviceId;
        DeviceName = deviceName;
        _scopeFactory = scopeFactory;
        _dialogs = dialogs;
        _outbox = outbox;
        _logger = logger;
        _ = RefreshAsync();
    }

    /// <summary>重新加载点位列表</summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var manager = scope.ServiceProvider.GetRequiredService<IPointManager>();
            var result = await manager.GetByDeviceAsync(_deviceId);
            if (result.IsFailure)
            {
                StatusText = $"加载点位失败：{result.Error!.Message}";
                return;
            }

            Items.Clear();
            foreach (var point in result.Value!)
                Items.Add(PointItem.From(point));
            StatusText = $"设备「{DeviceName}」共 {Items.Count} 个点位";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "点位列表加载失败: {DeviceId}", _deviceId);
            StatusText = "点位列表加载失败";
        }
    }

    /// <summary>新增点位：对话框保存后 AddAsync</summary>
    [RelayCommand]
    private async Task AddAsync()
    {
        var editor = new PointEditor { Id = Guid.NewGuid() };
        if (!_dialogs.EditPoint(editor))
            return;

        using var scope = _scopeFactory.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<IPointManager>();
        var result = await manager.AddAsync(_deviceId, editor.ToPoint());
        if (result.IsFailure)
        {
            StatusText = $"添加点位失败：{result.Error!.Message}";
            return;
        }
        // ADR-033 阶段 4：点位改动入 outbox，同步服务上报中心
        await RecordOutboxAsync(() => _outbox.RecordPointAsync(_deviceId, result.Value!));
        StatusText = $"点位「{editor.Name}」已添加";
        await RefreshAsync();
    }

    /// <summary>编辑点位：从选中行回填表单，UpdateAsync</summary>
    [RelayCommand]
    private async Task EditAsync()
    {
        if (SelectedPoint is null)
            return;

        var editor = PointEditor.FromPoint(SelectedPoint.Point);
        if (!_dialogs.EditPoint(editor))
            return;

        using var scope = _scopeFactory.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<IPointManager>();
        var result = await manager.UpdateAsync(_deviceId, editor.ToPoint());
        if (result.IsFailure)
        {
            StatusText = $"更新点位失败：{result.Error!.Message}";
            return;
        }
        // ADR-033 阶段 4：按点位 Id 记录（负载在上报时取本地最新状态）
        await RecordOutboxAsync(() => _outbox.RecordPointAsync(_deviceId, editor.ToPoint()));
        StatusText = $"点位「{editor.Name}」已更新";
        await RefreshAsync();
    }

    /// <summary>删除点位：确认后 RemoveAsync</summary>
    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (SelectedPoint is null)
            return;

        if (!_dialogs.Confirm("删除点位", $"确定删除点位「{SelectedPoint.Name}」？"))
            return;

        using var scope = _scopeFactory.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<IPointManager>();
        var result = await manager.RemoveAsync(_deviceId, SelectedPoint.Id);
        if (result.IsFailure)
        {
            StatusText = $"删除点位失败：{result.Error!.Message}";
            return;
        }
        // ADR-033 阶段 4：点位删除发 tombstone 上报
        await RecordOutboxAsync(() => _outbox.RecordPointDeleteAsync(_deviceId, SelectedPoint.Id));
        StatusText = $"点位「{SelectedPoint.Name}」已删除";
        await RefreshAsync();
    }

    public void Dispose()
    {
        // 暂无可释放资源；保留接口便于窗口关闭时统一清理
    }

    /// <summary>outbox 写入失败不阻断 UI 操作（本地库照常），仅记调试日志。</summary>
    private async Task RecordOutboxAsync(Func<Task<OperationResult>> record)
    {
        var result = await record();
        if (result.IsFailure)
            _logger.LogDebug("配置同步 outbox 记录失败：{Error}", result.Error!.Message);
    }
}

/// <summary>点位列表行（展示快照，含原始点位供编辑回填）</summary>
public sealed class PointItem
{
    public required DevicePoint Point { get; init; }
    public Guid Id => Point.Id;
    public string Name => Point.Name;
    public string Address => Point.Address;
    public string DataTypeText => Point.DataType.ToString();
    public string AccessText => Point.Access switch
    {
        PointAccess.ReadOnly => "只读",
        PointAccess.WriteOnly => "只写",
        _ => "读写"
    };
    public string ScaleText => $"{Point.ScaleFactor:0.###} / {Point.ScaleOffset:0.###}";
    public string ScanIntervalText => Point.ScanIntervalMs > 0 ? $"{Point.ScanIntervalMs} ms" : "继承";
    public bool Enabled => Point.Enabled;
    public string EnabledText => Point.Enabled ? "是" : "否";

    public static PointItem From(DevicePoint point) => new() { Point = point };
}
