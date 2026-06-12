using NitroGateway.Domain.Devices;
using NitroGateway.Shared;

namespace NitroGateway.Storage.Configuration;

/// <summary>
/// 点位持久化接口。负责 DevicePoint 的 CRUD 操作。
/// 由 PointManager 消费。
/// </summary>
public interface IPointRepository
{
    Task<OperationResult> SaveAsync(Guid deviceId, DevicePoint point, CancellationToken ct = default);
    Task<OperationResult> DeleteAsync(Guid deviceId, Guid pointId, CancellationToken ct = default);
    Task<OperationResult<IReadOnlyList<DevicePoint>>> GetByDeviceAsync(Guid deviceId, CancellationToken ct = default);
}
