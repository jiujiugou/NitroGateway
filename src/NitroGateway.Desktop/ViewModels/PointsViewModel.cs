using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NitroGateway.Desktop.Services.Dialogs;
using NitroGateway.Desktop.Services.Sync;
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
    private readonly ICsvFileService _csvFiles;
    private readonly PointBatchService _batch;
    private readonly ILogger<PointsViewModel> _logger;

    public ObservableCollection<PointItem> Items { get; } = [];

    /// <summary>设备名称（窗口标题用）</summary>
    public string DeviceName { get; }

    /// <summary>设备协议（Modbus / S7 / OPC UA），透传给点位表单与批量生成（地址提示、递增规则，docs/13）。</summary>
    public string ProtocolName { get; }

    [ObservableProperty] private PointItem? _selectedPoint;
    [ObservableProperty] private string _statusText = "";

    public PointsViewModel(
        Guid deviceId,
        string deviceName,
        string protocolName,
        IServiceScopeFactory scopeFactory,
        IDeviceDialogService dialogs,
        IConfigSyncOutboxStore outbox,
        ICsvFileService csvFiles,
        PointBatchService batch,
        ILogger<PointsViewModel> logger)
    {
        _deviceId = deviceId;
        DeviceName = deviceName;
        ProtocolName = protocolName;
        _scopeFactory = scopeFactory;
        _dialogs = dialogs;
        _outbox = outbox;
        _csvFiles = csvFiles;
        _batch = batch;
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
        var editor = new PointEditor { Id = Guid.NewGuid(), ProtocolName = ProtocolName };
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
        editor.ProtocolName = ProtocolName; // 编辑回填不保留协议，按设备协议给地址提示
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

    /// <summary>批量导入点位：选 CSV → 解析 → ImportAsync → 逐条入 outbox（配置同步上报）。</summary>
    [RelayCommand]
    private async Task ImportCsvAsync()
    {
        var csvText = _csvFiles.PickImportCsv();
        if (csvText is null)
            return; // 用户取消

        var parseResult = _batch.ParseCsv(csvText);
        if (parseResult.IsFailure)
        {
            StatusText = $"导入失败：{parseResult.Error!.Message}";
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<IPointManager>();
        var result = await manager.ImportAsync(_deviceId, parseResult.Value!);
        if (result.IsFailure)
        {
            StatusText = $"导入失败：{result.Error!.Message}";
            return;
        }

        // ADR-033 阶段 4：导入点位逐条入 outbox，同步服务上报中心（与 Add/Edit 同语义）
        foreach (var point in result.Value!)
            await RecordOutboxAsync(() => _outbox.RecordPointAsync(_deviceId, point));

        await RefreshAsync();
        StatusText = $"已导入 {result.Value!.Count} 个点位";
    }

    /// <summary>
    /// 批量生成点位（docs/13，对齐 Web 批量生成）：表单 → PointBatchService.Generate
    /// （按协议解释起始地址与步长）→ ImportAsync → 逐条入 outbox → 刷新。
    /// 起始地址格式/协议不兼容由 Generate 抛 ArgumentException，捕获后仅提示不落库。
    /// </summary>
    [RelayCommand]
    private async Task GenerateBatchAsync()
    {
        var editor = new PointBatchEditor { ProtocolName = ProtocolName };
        if (!_dialogs.EditPointBatch(editor))
            return;

        IReadOnlyList<DevicePoint> points;
        try
        {
            points = _batch.Generate(_deviceId, editor.NameTemplate, editor.StartAddress,
                editor.Count, editor.DataType, editor.Access, editor.ProtocolName);
        }
        catch (ArgumentException ex)
        {
            StatusText = $"批量生成失败：{ex.Message}";
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<IPointManager>();
        var result = await manager.ImportAsync(_deviceId, points);
        if (result.IsFailure)
        {
            StatusText = $"批量生成失败：{result.Error!.Message}";
            return;
        }

        // ADR-033 阶段 4：批量生成点位逐条入 outbox，同步服务上报中心（与导入同语义）
        foreach (var point in result.Value!)
            await RecordOutboxAsync(() => _outbox.RecordPointAsync(_deviceId, point));

        await RefreshAsync();
        StatusText = $"已批量生成 {result.Value!.Count} 个点位";
    }

    /// <summary>导出点位 CSV：GetByDeviceAsync → ExportCsv → 保存文件。</summary>
    [RelayCommand]
    private async Task ExportCsvAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<IPointManager>();
        var result = await manager.GetByDeviceAsync(_deviceId);
        if (result.IsFailure)
        {
            StatusText = $"导出失败：{result.Error!.Message}";
            return;
        }

        var csv = _batch.ExportCsv(result.Value!);
        if (!_csvFiles.SaveCsv($"points_{_deviceId}.csv", csv))
            return; // 用户取消

        StatusText = $"已导出 {result.Value!.Count} 个点位";
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
