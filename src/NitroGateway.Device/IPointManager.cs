using NitroGateway.Domain.Devices;
using NitroGateway.Shared;

namespace NitroGateway.DeviceManagement;

/// <summary>点位管理</summary>
public interface IPointManager
{
    Task<OperationResult<DevicePoint>> AddAsync(Guid deviceId, DevicePoint point, CancellationToken ct = default);
    Task<OperationResult> RemoveAsync(Guid deviceId, Guid pointId, CancellationToken ct = default);
    Task<OperationResult> UpdateAsync(Guid deviceId, DevicePoint point, CancellationToken ct = default);
    Task<OperationResult<IReadOnlyList<DevicePoint>>> ImportAsync(Guid deviceId, IReadOnlyList<DevicePoint> points, CancellationToken ct = default);
    Task<OperationResult<IReadOnlyList<DevicePoint>>> GetByDeviceAsync(Guid deviceId, CancellationToken ct = default);

    /// <summary>校验点位，委托 IAddressParser 检查地址格式</summary>
    Task<OperationResult<IReadOnlyList<PointValidationError>>> ValidateAsync(Guid deviceId, DevicePoint point, CancellationToken ct = default);
}
