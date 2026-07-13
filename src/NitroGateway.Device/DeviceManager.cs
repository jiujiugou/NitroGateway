using Microsoft.Extensions.Logging;
using NitroGateway.Domain.Devices;
using NitroGateway.Shared;
using NitroGateway.Storage.Configuration;

namespace NitroGateway.DeviceManagement;

/// <summary>设备生命周期管理实现</summary>
public sealed class DeviceManager : IDeviceManager
{
    private readonly IDeviceRepository _repository;
    private readonly ILogger<DeviceManager> _logger;

    public DeviceManager(
        IDeviceRepository repository,
        ILogger<DeviceManager> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    public async Task<OperationResult<Device>> RegisterAsync(Device device, CancellationToken ct = default)
    {
        if (device.Id == Guid.Empty)
            return OperationalError.Validation("设备 ID 不能为空");

        var result = await _repository.SaveAsync(device, ct);
        if (result.IsFailure) return result.Error!;

        _logger.LogInformation("设备已注册: {DeviceName} [{DeviceId}]", device.Name, device.Id);
        return device;
    }

    public async Task<OperationResult> UnregisterAsync(Guid deviceId, CancellationToken ct = default)
    {
        var device = await _repository.GetByIdAsync(deviceId, ct);
        if (device.IsFailure) return device.Error!;

        await _repository.DeleteAsync(deviceId, ct);
        _logger.LogInformation("设备已注销: {DeviceId}", deviceId);
        return OperationResult.Success();
    }

    public async Task<OperationResult<Device>> GetAsync(Guid deviceId, CancellationToken ct = default)
        => await _repository.GetByIdAsync(deviceId, ct);

    public async Task<OperationResult<IReadOnlyList<Device>>> GetAllAsync(CancellationToken ct = default)
        => await _repository.GetAllAsync(ct);

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

        // 状态门控：不允许 Manual 切换到 Online（必须由 HealthMonitor 触发恢复）
        if (status == DeviceStatus.Online && oldStatus == DeviceStatus.Offline)
        {
            _logger.LogWarning("设备 {DeviceId} 从 Offline 恢复为 Online 需通过 HealthMonitor", deviceId);
            return OperationalError.Validation("Offline 设备必须通过 HealthMonitor 恢复");
        }

        device.Status = status;
        await _repository.SaveAsync(device, ct);

        _logger.LogInformation("设备状态变更: {DeviceId} {Old} → {New}", deviceId, oldStatus, status);
        return OperationResult.Success();
    }

    public async Task<OperationResult> SetMaintenanceAsync(Guid deviceId, bool maintenance, CancellationToken ct = default)
    {
        var targetStatus = maintenance ? DeviceStatus.Maintenance : DeviceStatus.Unknown;
        return await UpdateStatusAsync(deviceId, targetStatus, ct);
    }
}
