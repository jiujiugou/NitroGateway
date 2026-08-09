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

    /// <summary>按 ID 查询设备，不存在时返回 Failure（General）</summary>
    Task<OperationResult<Device>> GetByIdAsync(Guid deviceId, CancellationToken ct = default);

    /// <summary>
    /// 获取全部设备列表。
    /// ADR-021 P2-1：全量加载无分页（当前设备规模小，可接受）；设备量增长时需新增分页重载（接口只增不删）。
    /// </summary>
    Task<OperationResult<IReadOnlyList<Device>>> GetAllAsync(CancellationToken ct = default);

    /// <summary>
    /// 按通信状态筛选设备。状态指配置/最近一次持久化状态，非 HealthMonitor 实时状态（ADR-021 P3-5）。
    /// ADR-021 P2-1：全量加载无分页；实时在线统计请读 HealthMonitor 快照。
    /// </summary>
    Task<OperationResult<IReadOnlyList<Device>>> GetByStatusAsync(DeviceStatus status, CancellationToken ct = default);
}
