using NitroGateway.Domain.Measurements;
using NitroGateway.Shared;

namespace NitroGateway.Storage.Buffer;

/// <summary>
/// 转发缓冲接口。FIFO 队列，断电不丢。
/// Collection 写入 → Forwarder 取出 → 确认成功 → 提交删除。
/// </summary>
public interface IForwardBuffer
{
    /// <summary>入队一批待转发数据。不应阻塞调用方</summary>
    Task<OperationResult> EnqueueAsync(BatchMeasurements batch, CancellationToken ct = default);

    /// <summary>
    /// 出队最多 maxCount 批数据，不移除。
    /// Forwarder 成功转发后调用 <see cref="CommitAsync"/> 删除。
    /// </summary>
    Task<OperationResult<IReadOnlyList<BatchMeasurements>>> DequeueAsync(int maxCount, CancellationToken ct = default);

    /// <summary>确认转发成功，移除已出队的批次</summary>
    Task<OperationResult> CommitAsync(IReadOnlyList<Guid> batchIds, CancellationToken ct = default);

    /// <summary>当前队列中待转发的批次数</summary>
    int Count { get; }
}
