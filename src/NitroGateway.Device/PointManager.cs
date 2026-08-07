using Microsoft.Extensions.Logging;
using NitroGateway.Domain.Devices;
using NitroGateway.Shared;
using NitroGateway.Storage.Configuration;

namespace NitroGateway.DeviceManagement;

/// <summary>点位管理实现</summary>
public sealed class PointManager : IPointManager
{
    private readonly IPointRepository _repository;
    private readonly IDeviceSnapshotCache _cache;
    private readonly ILogger<PointManager> _logger;

    public PointManager(
        IPointRepository repository,
        IDeviceSnapshotCache cache,
        ILogger<PointManager> logger)
    {
        _repository = repository;
        _cache = cache;
        _logger = logger;
    }

    public async Task<OperationResult<DevicePoint>> AddAsync(
        Guid deviceId, DevicePoint point, CancellationToken ct = default)
    {
        if (point.Id == Guid.Empty)
            return OperationalError.Validation("点位 ID 不能为空");

        var result = await _repository.SaveAsync(deviceId, point, ct);
        if (result.IsFailure) return result.Error!;

        _logger.LogInformation("点位已添加: {PointName} [{PointId}] → Device {DeviceId}", point.Name, point.Id, deviceId);
        // ADR-002 P2-2：点位配置变更使设备目录缓存失效
        _cache.Invalidate();
        return point;
    }

    public async Task<OperationResult> RemoveAsync(Guid deviceId, Guid pointId, CancellationToken ct = default)
    {
        var result = await _repository.DeleteAsync(deviceId, pointId, ct);
        if (result.IsFailure) return result.Error!;
        _cache.Invalidate();
        return OperationResult.Success();
    }

    public async Task<OperationResult> UpdateAsync(
        Guid deviceId, DevicePoint point, CancellationToken ct = default)
    {
        var result = await _repository.SaveAsync(deviceId, point, ct);
        if (result.IsFailure) return result.Error!;
        _cache.Invalidate();
        return OperationResult.Success();
    }

    public async Task<OperationResult<IReadOnlyList<DevicePoint>>> ImportAsync(
        Guid deviceId, IReadOnlyList<DevicePoint> points, CancellationToken ct = default)
    {
        // ADR-005 P2-1：优先走单事务批量保存（一次往返）；
        // 批量失败时回退逐条保存，保留「失败点名称」诊断信息。
        OperationResult batchResult;
        try
        {
            batchResult = await _repository.SaveBatchAsync(deviceId, points, ct);
        }
        catch (Exception ex)
        {
            batchResult = OperationalError.Storage($"批量保存异常: {ex.Message}");
        }

        if (batchResult.IsSuccess)
        {
            _logger.LogInformation("批量导入 {Count} 个点位 → Device {DeviceId}", points.Count, deviceId);
            _cache.Invalidate();
            return OperationResult<IReadOnlyList<DevicePoint>>.Success(points);
        }

        var failed = new List<string>();
        foreach (var point in points)
        {
            var result = await _repository.SaveAsync(deviceId, point, ct);
            if (result.IsFailure)
                failed.Add($"{point.Name} ({result.Error!.Message})");
        }

        // 部分点位成功落库也算配置变更，需要失效缓存
        if (failed.Count < points.Count)
            _cache.Invalidate();

        if (failed.Count > 0)
        {
            _logger.LogError("批量导入失败 {Failed}/{Total} 个点位: {Details}",
                failed.Count, points.Count, string.Join("; ", failed));
            return OperationalError.Storage($"导入失败 {failed.Count}/{points.Count} 个点位: {string.Join("; ", failed)}");
        }

        _logger.LogInformation("批量导入 {Count} 个点位 → Device {DeviceId}", points.Count, deviceId);
        return OperationResult<IReadOnlyList<DevicePoint>>.Success(points);
    }

    public async Task<OperationResult<IReadOnlyList<DevicePoint>>> GetByDeviceAsync(
        Guid deviceId, CancellationToken ct = default)
        => await _repository.GetByDeviceAsync(deviceId, ct);

    public Task<OperationResult<IReadOnlyList<PointValidationError>>> ValidateAsync(
        Guid deviceId, DevicePoint point, CancellationToken ct = default)
    {
        var errors = new List<PointValidationError>();

        if (string.IsNullOrWhiteSpace(point.Name))
            errors.Add(new PointValidationError { Field = "Name", Message = "点位名称不能为空" });
        if (string.IsNullOrWhiteSpace(point.Address))
            errors.Add(new PointValidationError { Field = "Address", Message = "地址不能为空" });
        if (point.ScanIntervalMs < 0)
            errors.Add(new PointValidationError { Field = "ScanIntervalMs", Message = "采集间隔不能为负数" });
        if (point.Deadband < 0)
            errors.Add(new PointValidationError { Field = "Deadband", Message = "死区不能为负数" });

        return Task.FromResult<OperationResult<IReadOnlyList<PointValidationError>>>(errors);
    }
}
