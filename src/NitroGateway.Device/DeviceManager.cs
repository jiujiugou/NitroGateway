using Microsoft.Extensions.Logging;
using NitroGateway.Domain.Devices;
using NitroGateway.Protocols;
using NitroGateway.Shared;
using NitroGateway.Storage.Configuration;

namespace NitroGateway.DeviceManagement;

/// <summary>设备生命周期管理实现</summary>
public sealed class DeviceManager : IDeviceManager
{
    private readonly IDeviceRepository _repository;
    private readonly IDeviceHealthMonitor _healthMonitor;
    private readonly IProtocolDriverPool _driverPool;
    private readonly IDeviceSnapshotCache _cache;
    private readonly ILogger<DeviceManager> _logger;

    public DeviceManager(
        IDeviceRepository repository,
        IDeviceHealthMonitor healthMonitor,
        IProtocolDriverPool driverPool,
        IDeviceSnapshotCache cache,
        ILogger<DeviceManager> logger)
    {
        _repository = repository;
        _healthMonitor = healthMonitor;
        _driverPool = driverPool;
        _cache = cache;
        _logger = logger;
    }

    public async Task<OperationResult<Device>> RegisterAsync(Device device, CancellationToken ct = default)
    {
        if (device.Id == Guid.Empty)
            return OperationalError.Validation("设备 ID 不能为空");

        var result = await _repository.SaveAsync(device, ct);
        if (result.IsFailure) 
            return result.Error!;

        // 设备新建或更新：驱逐旧驱动，下一轮采集用新连接参数重建
        _driverPool.Evict(device.Id);
        _logger.LogInformation("设备已注册: {DeviceName} [{DeviceId}]", device.Name, device.Id);
        _healthMonitor.UpdateStatus(device.Id, device.Status);
        // ADR-002 P2-2：配置变更使设备目录缓存失效
        _cache.Invalidate();
        return device;
    }

    public async Task<OperationResult> UnregisterAsync(Guid deviceId, CancellationToken ct = default)
    {
        var device = await _repository.GetByIdAsync(deviceId, ct);
        if (device.IsFailure) return device.Error!;

        await _repository.DeleteAsync(deviceId, ct);
        _driverPool.Evict(deviceId);
        _healthMonitor.Remove(deviceId);
        _logger.LogInformation("设备已注销: {DeviceId}", deviceId);
        _cache.Invalidate();
        return OperationResult.Success();
    }

    public async Task<OperationResult<Device>> GetAsync(Guid deviceId, CancellationToken ct = default)
        => await _repository.GetByIdAsync(deviceId, ct);

    // ADR-002 P2-2：走内存缓存，避免采集热路径每 1s 全量 EF Include(Points) 映射
    public Task<OperationResult<IReadOnlyList<Device>>> GetAllAsync(CancellationToken ct = default)
        => _cache.GetAllAsync(ct);

    public async Task<OperationResult<IReadOnlyList<Device>>> GetByStatusAsync(
        DeviceStatus status, CancellationToken ct = default)
        => await _repository.GetByStatusAsync(status, ct);

    public async Task<OperationResult> UpdateStatusAsync(
        Guid deviceId, DeviceStatus status, CancellationToken ct = default)
    {
        var result = await _repository.GetByIdAsync(deviceId, ct);
        if (result.IsFailure) return result.Error!;

        var device = result.Value!;
        var oldStatus = device.Status;

        if (oldStatus == status) return OperationResult.Success();

        device.Status = status;
        await _repository.SaveAsync(device, ct);

        // 下线/维护时释放连接；恢复后下一轮采集重建
        _driverPool.Evict(deviceId);
        _cache.Invalidate();
        _logger.LogInformation("设备状态变更: {DeviceId} {Old} → {New}", deviceId, oldStatus, status);
        return OperationResult.Success();
    }

    public async Task<OperationResult> SetMaintenanceAsync(Guid deviceId, bool maintenance, CancellationToken ct = default)
    {
        var targetStatus = maintenance ? DeviceStatus.Maintenance : DeviceStatus.Unknown;
        return await UpdateStatusAsync(deviceId, targetStatus, ct);
    }
}
