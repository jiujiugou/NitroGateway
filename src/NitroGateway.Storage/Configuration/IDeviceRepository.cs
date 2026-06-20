using NitroGateway.Domain.Devices;
using NitroGateway.Shared;

namespace NitroGateway.Storage.Configuration;

/// <summary>
/// 设备持久化接口。负责设备的 CRUD 操作。
/// 由 DeviceManager 消费，不关心底层实现（SQLite / PostgreSQL / ...）
/// </summary>
public interface IDeviceRepository
{
    /// <summary>保存或更新设备。Id 已存在时覆盖</summary>
    Task<OperationResult> SaveAsync(Device device, CancellationToken ct = default);

    /// <summary>删除指定设备</summary>
    Task<OperationResult> DeleteAsync(Guid deviceId, CancellationToken ct = default);

    /// <summary>按 ID 查询设备，不存在时返回 null</summary>
    Task<OperationResult<Device>> GetByIdAsync(Guid deviceId, CancellationToken ct = default);

    /// <summary>获取全部设备列表</summary>
    Task<OperationResult<IReadOnlyList<Device>>> GetAllAsync(CancellationToken ct = default);

    /// <summary>按通信状态筛选设备</summary>
    Task<OperationResult<IReadOnlyList<Device>>> GetByStatusAsync(DeviceStatus status, CancellationToken ct = default);
}
