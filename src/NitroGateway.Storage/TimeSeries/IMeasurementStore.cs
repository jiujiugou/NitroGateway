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

    /// <summary>删除指定时间之前的历史数据，用于存储空间管理</summary>
    Task<OperationResult> PurgeAsync(DateTime before, CancellationToken ct = default);
}
