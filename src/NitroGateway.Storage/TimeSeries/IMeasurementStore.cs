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
    Task<OperationResult> WriteAsync(IReadOnlyList<PointSnapshot> snapshots, CancellationToken ct = default);

    Task<OperationResult<IReadOnlyList<PointSnapshot>>> QueryAsync(
        Guid deviceId, Guid pointId, DateTime from, DateTime to, CancellationToken ct = default);

    Task<OperationResult<IReadOnlyList<PointSnapshot>>> QueryByDeviceAsync(
        Guid deviceId, DateTime from, DateTime to, CancellationToken ct = default);

    Task<OperationResult<IReadOnlyList<PointSnapshot>>> QueryPagedAsync(
        Guid deviceId, Guid? pointId, DateTime from, DateTime to, int limit, int offset, CancellationToken ct = default);

    /// <summary>
    /// 分页查询历史快照（按站点过滤，ADR-035 第 1 步）。
    /// siteId 为空时不过滤（兼容未标注站点数据）；默认实现委托无站点重载，兼容既有实现（接口只增不删）。
    /// </summary>
    Task<OperationResult<IReadOnlyList<PointSnapshot>>> QueryPagedAsync(
        Guid deviceId, Guid? pointId, DateTime from, DateTime to, int limit, int offset, string? siteId,
        CancellationToken ct = default)
        => QueryPagedAsync(deviceId, pointId, from, to, limit, offset, ct);

    Task<OperationResult<IReadOnlyList<PointSnapshot>>> QueryLatestAsync(
        Guid deviceId, Guid? pointId, CancellationToken ct = default);

    /// <summary>
    /// 查询设备最新快照（按站点过滤，ADR-035 第 1 步）。
    /// siteId 为空时不过滤（兼容未标注站点数据）；默认实现委托无站点重载，兼容既有实现（接口只增不删）。
    /// </summary>
    Task<OperationResult<IReadOnlyList<PointSnapshot>>> QueryLatestAsync(
        Guid deviceId, Guid? pointId, string? siteId, CancellationToken ct = default)
        => QueryLatestAsync(deviceId, pointId, ct);

    /// <summary>删除指定时间之前的历史数据，用于存储空间管理</summary>
    Task<OperationResult> PurgeAsync(DateTime before, CancellationToken ct = default);
}
