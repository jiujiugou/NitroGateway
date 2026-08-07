using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Measurements;
using NitroGateway.Shared;

namespace NitroGateway.Storage.TimeSeries;

/// <summary>
/// 时序数据存储接口。负责 PointSnapshot 的批量写入和时间范围查询。
/// 由 Collection 消费写入，Webapi/Admin 消费查询。
/// 底层实现不关心（SQLite / InfluxDB / TimescaleDB / ...）
/// </summary>
public interface IMeasurementStore
{
    /// <summary>批量写入快照。内部应做批量优化而非逐条 INSERT</summary>
    Task<OperationResult> WriteAsync(IReadOnlyList<PointSnapshot> snapshots, CancellationToken ct = default);

    /// <summary>按设备、点位、时间范围查询历史快照</summary>
    Task<OperationResult<IReadOnlyList<PointSnapshot>>> QueryAsync(
        Guid deviceId, Guid pointId, DateTime from, DateTime to, CancellationToken ct = default);

    /// <summary>按设备查询时间范围内的所有快照（用于批量取最新值）</summary>
    Task<OperationResult<IReadOnlyList<PointSnapshot>>> QueryByDeviceAsync(
        Guid deviceId, DateTime from, DateTime to, CancellationToken ct = default);

    /// <summary>
    /// 分页查询历史快照（按时间升序）。pointId 为 null 时查设备下全部点位（QueryByDeviceAsync 的分页版）。
    /// ADR-005 P2-2：避免大结果集一次性全量加载。
    /// </summary>
    /// <param name="limit">单页条数，实现应夹紧到 [1, 1000]</param>
    /// <param name="offset">跳过条数，实现应夹紧到 ≥0</param>
    Task<OperationResult<IReadOnlyList<PointSnapshot>>> QueryPagedAsync(
        Guid deviceId, Guid? pointId, DateTime from, DateTime to, int limit, int offset, CancellationToken ct = default);

    /// <summary>
    /// 查询设备最新快照。pointId 为 null 时返回设备下每个点位的最新一条（每点一条）。
    /// ADR-002 P2-4：替代控制器"拉 1 小时全量再内存过滤"，用 SQL 直接取最新，避免大结果集。
    /// </summary>
    Task<OperationResult<IReadOnlyList<PointSnapshot>>> QueryLatestAsync(
        Guid deviceId, Guid? pointId, CancellationToken ct = default);

    /// <summary>删除指定时间之前的历史数据，用于存储空间管理</summary>
    Task<OperationResult> PurgeAsync(DateTime before, CancellationToken ct = default);
}
