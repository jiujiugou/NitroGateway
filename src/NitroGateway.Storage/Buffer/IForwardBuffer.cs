using NitroGateway.Domain.Measurements;
using NitroGateway.Shared;

namespace NitroGateway.Storage.Buffer;

/// <summary>
/// 转发缓冲接口。FIFO 队列，断电不丢。
/// Collection 写入 → Forwarder 取出 → 确认成功 → 提交删除。
/// </summary>
public interface IForwardBuffer
{
    /// <summary>入队一批待转发数据</summary>
    Task<OperationResult> EnqueueAsync(BatchMeasurements batch, CancellationToken ct = default);

    /// <summary>出队一批数据（不移除，Forwarder 成功后才 Commit）</summary>
    Task<OperationResult<IReadOnlyList<BatchMeasurements>>> DequeueAsync(int maxCount, CancellationToken ct = default);

    /// <summary>确认转发成功，移除已出队的数据</summary>
    Task<OperationResult> CommitAsync(IReadOnlyList<Guid> batchIds, CancellationToken ct = default);

    /// <summary>当前队列长度</summary>
    int Count { get; }
}
