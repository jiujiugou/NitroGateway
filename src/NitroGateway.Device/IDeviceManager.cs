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

    /// <summary>
    /// 查询全部设备（含中心侧 tombstone，ADR-033 阶段 3/4）。
    /// 供配置同步导出使用；Web UI 与采集热路径请用 <see cref="GetAllAsync"/>（已过滤删除）。
    /// </summary>
    Task<OperationResult<IReadOnlyList<Device>>> GetAllIncludingDeletedAsync(CancellationToken ct = default);

    /// <summary>
    /// 按 ID 查询设备（含 tombstone，ADR-033 阶段 3/4）。
    /// 供同步接收端判断"中心已删拒绝复活"；业务查询请用 <see cref="GetAsync"/>。
    /// </summary>
    Task<OperationResult<Device>> GetIncludingDeletedAsync(Guid deviceId, CancellationToken ct = default);

    /// <summary>
    /// 中心权威软删（tombstone，ADR-033 阶段 3/4）：置 IsDeleted=true 并盖章 UpdatedAt，
    /// 同步导出携带该标记驱动现场删除；同步接收端对已删设备拒绝现场复活。
    /// 已删除/不存在时幂等成功。
    /// </summary>
    Task<OperationResult> SoftDeleteAsync(Guid deviceId, CancellationToken ct = default);

    /// <summary>更新状态（唯一入口，不允许外部直接赋值 Device.Status）</summary>
    Task<OperationResult> UpdateStatusAsync(Guid deviceId, DeviceStatus status, CancellationToken ct = default);

    Task<OperationResult> SetMaintenanceAsync(Guid deviceId, bool maintenance, CancellationToken ct = default);
}
