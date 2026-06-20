using NitroGateway.Domain.Devices;
using NitroGateway.Shared;

namespace NitroGateway.Storage.Configuration;

/// <summary>
/// 点位持久化接口。负责 DevicePoint 的 CRUD 操作。
/// 由 PointManager 消费。
/// </summary>
public interface IPointRepository
{
    /// <summary>保存或更新点位。Id 已存在时覆盖</summary>
    Task<OperationResult> SaveAsync(Guid deviceId, DevicePoint point, CancellationToken ct = default);

    /// <summary>删除指定设备下的指定点位</summary>
    Task<OperationResult> DeleteAsync(Guid deviceId, Guid pointId, CancellationToken ct = default);

    /// <summary>获取指定设备下的全部点位</summary>
    Task<OperationResult<IReadOnlyList<DevicePoint>>> GetByDeviceAsync(Guid deviceId, CancellationToken ct = default);
}
