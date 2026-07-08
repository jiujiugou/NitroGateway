using Microsoft.Extensions.Logging;
using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Events;
using NitroGateway.Shared;
using NitroGateway.Storage.Configuration;

namespace NitroGateway.DeviceManagement;

/// <summary>点位管理实现</summary>
public sealed class PointManager : IPointManager
{
    private readonly IPointRepository _repository;
    private readonly IEnumerable<IDeviceChangeSink> _sinks;
    private readonly ILogger<PointManager> _logger;

    public PointManager(
        IPointRepository repository,
        IEnumerable<IDeviceChangeSink> sinks,
        ILogger<PointManager> logger)
    {
        _repository = repository;
        _sinks = sinks;
        _logger = logger;
    }

    private void NotifySinks(Guid deviceId)
    {
        var e = new DeviceChangeEvent { Type = DeviceChangeType.PointsChanged, DeviceId = deviceId };
        foreach (var sink in _sinks)
        {
            try { sink.OnDeviceChanged(e); }
            catch (Exception ex) { _logger.LogError(ex, "DeviceChangeSink 异常"); }
        }
    }

    public async Task<OperationResult<DevicePoint>> AddAsync(
        Guid deviceId, DevicePoint point, CancellationToken ct = default)
    {
        if (point.Id == Guid.Empty)
            return OperationalError.Validation("点位 ID 不能为空");

        await _repository.SaveAsync(deviceId, point, ct);
        _logger.LogInformation("点位已添加: {PointName} [{PointId}] → Device {DeviceId}", point.Name, point.Id, deviceId);
        NotifySinks(deviceId);
        return point;
    }

    public async Task<OperationResult> RemoveAsync(Guid deviceId, Guid pointId, CancellationToken ct = default)
    {
        await _repository.DeleteAsync(deviceId, pointId, ct);
        NotifySinks(deviceId);
        return OperationResult.Success();
    }

    public async Task<OperationResult> UpdateAsync(
        Guid deviceId, DevicePoint point, CancellationToken ct = default)
    {
        await _repository.SaveAsync(deviceId, point, ct);
        NotifySinks(deviceId);
        return OperationResult.Success();
    }

    public async Task<OperationResult<IReadOnlyList<DevicePoint>>> ImportAsync(
        Guid deviceId, IReadOnlyList<DevicePoint> points, CancellationToken ct = default)
    {
        foreach (var point in points)
            await _repository.SaveAsync(deviceId, point, ct);

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
