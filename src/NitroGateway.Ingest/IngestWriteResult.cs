namespace NitroGateway.Ingest;

/// <summary>
/// 一次遥测批次写入的结果统计（ADR-025 D2 幂等口径）。
/// 去重数 = 收到数 - 实际新增数（主键冲突被 INSERT OR IGNORE 忽略的记录数）。
/// </summary>
public sealed record IngestWriteResult(int ReceivedCount, int InsertedCount)
{
    /// <summary>主键冲突被忽略的记录数（至少一次语义下的重复投递）</summary>
    public int DeduplicatedCount => ReceivedCount - InsertedCount;
}
