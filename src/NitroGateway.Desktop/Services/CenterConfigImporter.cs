using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NitroGateway.DeviceManagement;
using NitroGateway.Domain.Devices;
using NitroGateway.Shared;

namespace NitroGateway.Desktop.Services;

/// <summary>一次「从中心导入」的结果统计。</summary>
public sealed record ImportSummary
{
    /// <summary>已导入（新增或更新）的设备数</summary>
    public int ImportedDevices { get; init; }

    /// <summary>已导入的点位数（按快照点数累计）</summary>
    public int ImportedPoints { get; init; }

    /// <summary>已移除的本地设备数（中心快照中不存在）</summary>
    public int RemovedDevices { get; init; }

    /// <summary>已移除的本地点位数（该设备快照中不存在）</summary>
    public int RemovedPoints { get; init; }
}

/// <summary>以中心为准重置本地设备/点位配置（ADR-033 阶段 2）。</summary>
public interface ICenterConfigImporter
{
    /// <summary>
    /// 将本地配置重置为中心快照：中心没有的本地设备整机移除（级联点位）；
    /// 快照设备按 Id upsert（复用 IDeviceManager.RegisterAsync），点位先删本地多余、再批量导入快照点位。
    /// 单设备失败不中断，汇总错误返回；成功时返回统计。
    /// </summary>
    Task<OperationResult<ImportSummary>> ImportAsync(IReadOnlyList<Device> snapshot, CancellationToken ct = default);
}

/// <summary>
/// 导入服务实现。设备/点位管理均为 Scoped（ADR-029），每个设备操作建独立作用域，
/// 避免长生命周期 EF 上下文的跟踪污染；导入后缓存失效由各 Manager 完成，采集下一轮即生效。
/// </summary>
public sealed class CenterConfigImporter : ICenterConfigImporter
{
    private readonly IDeviceSnapshotCache _cache;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<CenterConfigImporter> _logger;

    public CenterConfigImporter(
        IDeviceSnapshotCache cache,
        IServiceScopeFactory scopeFactory,
        ILogger<CenterConfigImporter> logger)
    {
        _cache = cache;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<OperationResult<ImportSummary>> ImportAsync(
        IReadOnlyList<Device> snapshot, CancellationToken ct = default)
    {
        // 1. 读取最新本地设备（先失效缓存，避免读到导入前的旧快照）
        _cache.Invalidate();
        IReadOnlyList<Device> local;
        using (var scope = _scopeFactory.CreateScope())
        {
            var manager = scope.ServiceProvider.GetRequiredService<IDeviceManager>();
            var localResult = await manager.GetAllAsync(ct);
            if (localResult.IsFailure)
                return localResult.Error!;
            local = localResult.Value!;
        }

        var snapshotById = snapshot.ToDictionary(d => d.Id);
        var errors = new List<string>();
        int importedDevices = 0, importedPoints = 0, removedDevices = 0, removedPoints = 0;

        // 2. 移除中心快照中不存在的本地设备（UnregisterAsync 级联删点位）
        foreach (var device in local)
        {
            if (snapshotById.ContainsKey(device.Id))
                continue;

            using var scope = _scopeFactory.CreateScope();
            var manager = scope.ServiceProvider.GetRequiredService<IDeviceManager>();
            var result = await manager.UnregisterAsync(device.Id, ct);
            if (result.IsFailure)
                errors.Add($"移除本地设备「{device.Name}」失败：{result.Error!.Message}");
            else
                removedDevices++;
        }

        // 3. 按快照 upsert 设备与点位
        foreach (var device in snapshot)
        {
            using var scope = _scopeFactory.CreateScope();
            var manager = scope.ServiceProvider.GetRequiredService<IDeviceManager>();
            var points = scope.ServiceProvider.GetRequiredService<IPointManager>();

            var registerResult = await manager.RegisterAsync(device, ct);
            if (registerResult.IsFailure)
            {
                errors.Add($"导入设备「{device.Name}」失败：{registerResult.Error!.Message}");
                continue;
            }
            importedDevices++;

            var pointResult = await ReplacePointsAsync(points, device, ct);
            if (pointResult.IsFailure)
                errors.Add(pointResult.Error!.Message);
            else
            {
                importedPoints += device.Points.Count;
                removedPoints += pointResult.Value!;
            }
        }

        _logger.LogInformation(
            "从中心导入完成：{ImportedDevices} 台设备 / {ImportedPoints} 个点位，移除 {RemovedDevices} 台 / {RemovedPoints} 个点位，失败 {Errors} 项",
            importedDevices, importedPoints, removedDevices, removedPoints, errors.Count);

        return errors.Count > 0
            ? OperationalError.General($"导入完成但有 {errors.Count} 项失败：{string.Join("；", errors.Take(3))}")
            : OperationResult<ImportSummary>.Success(new ImportSummary
            {
                ImportedDevices = importedDevices,
                ImportedPoints = importedPoints,
                RemovedDevices = removedDevices,
                RemovedPoints = removedPoints
            });
    }

    /// <summary>
    /// 将某设备点位重置为快照：删除本地多余点位，批量导入快照点位（按 Id upsert）。
    /// 返回移除的本地点位数。
    /// </summary>
    private static async Task<OperationResult<int>> ReplacePointsAsync(
        IPointManager points, Device device, CancellationToken ct)
    {
        var existingResult = await points.GetByDeviceAsync(device.Id, ct);
        if (existingResult.IsFailure)
            return OperationalError.General($"读取设备「{device.Name}」点位失败：{existingResult.Error!.Message}");

        var snapshotPointIds = device.Points.Select(p => p.Id).ToHashSet();
        var removed = 0;
        foreach (var point in existingResult.Value!)
        {
            if (snapshotPointIds.Contains(point.Id))
                continue;
            var removeResult = await points.RemoveAsync(device.Id, point.Id, ct);
            if (removeResult.IsFailure)
                return OperationalError.General($"移除设备「{device.Name}」点位「{point.Name}」失败：{removeResult.Error!.Message}");
            removed++;
        }

        if (device.Points.Count == 0)
            return removed;

        var importResult = await points.ImportAsync(device.Id, device.Points.ToList(), ct);
        return importResult.IsFailure
            ? OperationalError.General($"导入设备「{device.Name}」点位失败：{importResult.Error!.Message}")
            : removed;
    }
}
