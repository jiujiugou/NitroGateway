using NitroGateway.Domain.Measurements;
using NitroGateway.Shared;

namespace NitroGateway.Ingest;

/// <summary>
/// 中心入库接口（ADR-025 D2/D3）。
/// 实现需按记录主键幂等：measurements.id / alarms.id 冲突即重复投递，
/// 遥测用 INSERT OR IGNORE（丢弃重复），告警用 UPSERT（状态迁移覆盖）。
/// </summary>
public interface IIngestStore
{
    /// <summary>
    /// 批量写入遥测记录（INSERT OR IGNORE）。
    /// </summary>
    /// <param name="records">一批遥测记录</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>成功时返回 <see cref="IngestWriteResult"/>（新增/去重统计）</returns>
    Task<OperationResult<IngestWriteResult>> WriteMeasurementsAsync(
        IReadOnlyList<MeasurementRecord> records, CancellationToken ct = default);

    /// <summary>
    /// 批量写入遥测记录（INSERT OR IGNORE），并标注站点（ADR-035 第 1 步）。
    /// 站点标识来自上行 topic 第三层；旧版 topic 解析为空串时按未标注站点写入。
    /// 默认实现委托给无站点重载，兼容既有实现（接口只增不删）。
    /// </summary>
    Task<OperationResult<IngestWriteResult>> WriteMeasurementsAsync(
        IReadOnlyList<MeasurementRecord> records, string siteId, CancellationToken ct = default)
        => WriteMeasurementsAsync(records, ct);

    /// <summary>
    /// 按告警 ID 幂等写入/更新告警（UPSERT，状态迁移可覆盖旧状态）。
    /// </summary>
    /// <param name="alarm">告警上行消息</param>
    /// <param name="ct">取消令牌</param>
    Task<OperationResult> UpsertAlarmAsync(IngestAlarmMessage alarm, CancellationToken ct = default);

    /// <summary>
    /// 按告警 ID 幂等写入/更新告警（UPSERT），并标注站点（ADR-035 第 1 步）。
    /// 默认实现委托给无站点重载，兼容既有实现（接口只增不删）。
    /// </summary>
    Task<OperationResult> UpsertAlarmAsync(IngestAlarmMessage alarm, string siteId, CancellationToken ct = default)
        => UpsertAlarmAsync(alarm, ct);
}
