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
    /// 按告警 ID 幂等写入/更新告警（UPSERT，状态迁移可覆盖旧状态）。
    /// </summary>
    /// <param name="alarm">告警上行消息</param>
    /// <param name="ct">取消令牌</param>
    Task<OperationResult> UpsertAlarmAsync(IngestAlarmMessage alarm, CancellationToken ct = default);
}
