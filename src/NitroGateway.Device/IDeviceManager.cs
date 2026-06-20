using NitroGateway.Domain.Devices;
using NitroGateway.Shared;

namespace NitroGateway.DeviceManagement;

/// <summary>设备生命周期管理</summary>
public interface IDeviceManager
{
    Task<OperationResult<Device>> RegisterAsync(Device device, CancellationToken ct = default);
    Task<OperationResult> UnregisterAsync(Guid deviceId, CancellationToken ct = default);
    Task<OperationResult<Device>> GetAsync(Guid deviceId, CancellationToken ct = default);
    Task<OperationResult<IReadOnlyList<Device>>> GetAllAsync(CancellationToken ct = default);
    Task<OperationResult<IReadOnlyList<Device>>> GetByStatusAsync(DeviceStatus status, CancellationToken ct = default);

    /// <summary>更新状态（唯一入口，不允许外部直接赋值 Device.Status）</summary>
    Task<OperationResult> UpdateStatusAsync(Guid deviceId, DeviceStatus status, CancellationToken ct = default);

    Task<OperationResult> SetMaintenanceAsync(Guid deviceId, bool maintenance, CancellationToken ct = default);
}
