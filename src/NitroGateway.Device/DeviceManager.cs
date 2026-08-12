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

        // ADR-018 P2-2：删除失败不再静默（修复前忽略返回值，仓储异常不归类时该分支不可达）
        var deleted = await _repository.DeleteAsync(deviceId, ct);
        if (deleted.IsFailure) return deleted.Error!;

        _driverPool.Evict(deviceId);
        _healthMonitor.Remove(deviceId);
        _logger.LogInformation("设备已注销: {DeviceId}", deviceId);
        _cache.Invalidate();
        return OperationResult.Success();
    }

    public async Task<OperationResult<Device>> GetAsync(Guid deviceId, CancellationToken ct = default)
        => await _repository.GetByIdAsync(deviceId, ct);

    // ADR-002 P2-2：走内存缓存，避免采集热路径每 1s 全量 EF Include(Points) 映射
    // ADR-033 阶段 3/4：过滤中心侧 tombstone（Web UI 与采集热路径只看到存活设备）
    public async Task<OperationResult<IReadOnlyList<Device>>> GetAllAsync(CancellationToken ct = default)
    {
        var all = await _cache.GetAllAsync(ct);
        if (all.IsFailure) return all.Error!;
        return OperationResult<IReadOnlyList<Device>>.Success(
            all.Value!.Where(d => !d.IsDeleted).ToList());
    }

    /// <inheritdoc />
    /// <remarks>缓存内容为仓库全量（含 tombstone），同步导出需要完整视图（ADR-033 阶段 3/4）。</remarks>
    /// <inheritdoc />
    /// <remarks>siteId 非空时按站点过滤（ADR-035 方案 A：设备单一归属，中心下发按现场隔离）。</remarks>
    public async Task<OperationResult<IReadOnlyList<Device>>> GetAllAsync(
        string? siteId, CancellationToken ct = default)
    {
        var all = await GetAllAsync(ct);
        if (all.IsFailure) return all.Error!;
        return OperationResult<IReadOnlyList<Device>>.Success(
            string.IsNullOrEmpty(siteId) ? all.Value! : all.Value!.Where(d => d.SiteId == siteId).ToList());
    }

    /// <inheritdoc />
    /// <remarks>siteId 非空时按站点过滤（含 tombstone，同步导出按现场隔离）。</remarks>
    public async Task<OperationResult<IReadOnlyList<Device>>> GetAllIncludingDeletedAsync(
        string? siteId, CancellationToken ct = default)
    {
        var all = await GetAllIncludingDeletedAsync(ct);
        if (all.IsFailure) return all.Error!;
        return OperationResult<IReadOnlyList<Device>>.Success(
            string.IsNullOrEmpty(siteId) ? all.Value! : all.Value!.Where(d => d.SiteId == siteId).ToList());
    }

    public Task<OperationResult<IReadOnlyList<Device>>> GetAllIncludingDeletedAsync(CancellationToken ct = default)
        => _cache.GetAllAsync(ct);

    /// <inheritdoc />
    public async Task<OperationResult<Device>> GetIncludingDeletedAsync(Guid deviceId, CancellationToken ct = default)
        => await _repository.GetByIdAsync(deviceId, ct);

    /// <inheritdoc />
    public async Task<OperationResult> SoftDeleteAsync(Guid deviceId, CancellationToken ct = default)
    {
        var existing = await _repository.GetByIdAsync(deviceId, ct);
        if (existing.IsFailure)
        {
            // 同步路径按 ID 处理：设备不存在视为已删除，幂等成功
            _logger.LogDebug("软删目标不存在（幂等成功）: {DeviceId}", deviceId);
            return OperationResult.Success();
        }

        var device = existing.Value!;
        if (device.IsDeleted)
            return OperationResult.Success();

        device.IsDeleted = true;
        // ADR-033 阶段 3/4：中心时钟为权威——删除盖章取中心当前时间
        device.UpdatedAt = DateTime.UtcNow;
        var saved = await _repository.SaveAsync(device, ct);
        if (saved.IsFailure) return saved.Error!;

        _driverPool.Evict(deviceId);
        _healthMonitor.Remove(deviceId);
        _cache.Invalidate();
        _logger.LogInformation("设备已软删（tombstone）: {DeviceName} [{DeviceId}]", device.Name, deviceId);
        return OperationResult.Success();
    }

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
        // ADR-018 P2-2：状态持久化失败不再静默，返回错误由调用方处置
        var saved = await _repository.SaveAsync(device, ct);
        if (saved.IsFailure) return saved.Error!;

        // 下线/维护时释放连接；恢复后下一轮采集重建
        _driverPool.Evict(deviceId);
        _cache.Invalidate();
        _logger.LogInformation("设备状态变更: {DeviceName} [{DeviceId}] {Old} → {New}",
            device.Name, deviceId, oldStatus, status);
        return OperationResult.Success();
    }

    public async Task<OperationResult> SetMaintenanceAsync(Guid deviceId, bool maintenance, CancellationToken ct = default)
    {
        var targetStatus = maintenance ? DeviceStatus.Maintenance : DeviceStatus.Unknown;
        return await UpdateStatusAsync(deviceId, targetStatus, ct);
    }
}

