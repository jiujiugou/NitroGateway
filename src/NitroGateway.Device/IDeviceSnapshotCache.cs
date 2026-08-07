using NitroGateway.Domain.Devices;
using NitroGateway.Shared;
using NitroGateway.Storage.Configuration;

namespace NitroGateway.DeviceManagement;

/// <summary>
/// 设备+点位目录内存缓存（ADR-002 P2-2）。
/// 采集热路径每 1s 调 GetAllAsync，若每次都 EF Include(Points) 全量映射会放大 DB 压力；
/// 该缓存让配置读取走内存，配置写入（注册/注销/点位增删改/状态变更）触发 Invalidate 立即失效，
/// 另有 TTL 兜底防止漏失效。与 docs/02-架构理解.md 记载的「DeviceCache 内存更新」设计一致。
/// 注意：本缓存只保证「设备+点位配置」的低频读取；运行状态（Online/Offline/Maintenance）
/// 以 <see cref="IDeviceHealthMonitor"/> 实时快照为准（采集器维护过滤等关键路径不读缓存 Status）。
/// </summary>
public interface IDeviceSnapshotCache
{
    /// <summary>返回设备全量（含点位）；缓存未命中/失效/超 TTL 时从仓储加载。返回对象不得被调用方修改。</summary>
    Task<OperationResult<IReadOnlyList<Device>>> GetAllAsync(CancellationToken ct = default);

    /// <summary>配置写入后调用，使缓存立即失效，下一轮读取加载最新配置。</summary>
    void Invalidate();
}
