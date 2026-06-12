using NitroGateway.Domain.Devices;
using NitroGateway.Shared;

namespace NitroGateway.Storage.Configuration;

/// <summary>
/// 设备持久化接口。负责设备的 CRUD 操作。
/// 由 DeviceManager 消费，不关心底层实现（SQLite / PostgreSQL / ...）
/// </summary>
public interface IDeviceRepository
{
    Task<OperationResult> SaveAsync(Device device, CancellationToken ct = default);
    Task<OperationResult> DeleteAsync(Guid deviceId, CancellationToken ct = default);
    Task<OperationResult<Device>> GetByIdAsync(Guid deviceId, CancellationToken ct = default);
    Task<OperationResult<IReadOnlyList<Device>>> GetAllAsync(CancellationToken ct = default);
    Task<OperationResult<IReadOnlyList<Device>>> GetByStatusAsync(DeviceStatus status, CancellationToken ct = default);
}
