using NitroGateway.Domain.Measurements;
using NitroGateway.Shared;

namespace NitroGateway.Storage.Buffer;

/// <summary>
/// 转发缓冲接口。FIFO 队列，断电不丢。
/// Collection 写入 → Forwarder 取出 → 确认成功 → 提交删除。
/// 转发失败的消息重试超限后直接丢弃（普通遥测旧值不值钱，简化 2026-08-22；死信方法保留仅因接口只增不删）。
/// </summary>
public interface IForwardBuffer
{
    public const string MqttChannel = "mqtt";

    public const string HttpChannel = "http";

    /// <summary>入队一批待转发数据。不应阻塞调用方</summary>
    Task<OperationResult> EnqueueAsync(BatchMeasurements batch, CancellationToken ct = default);

    Task<OperationResult> EnqueueAsync(BatchMeasurements batch, string channel, CancellationToken ct = default)
        => EnqueueAsync(batch, ct);

    /// <summary>
    /// 出队最多 maxCount 批数据，不移除。
    /// 只返回 Pending 且未超过重试上限的批次。Forwarder 成功转发后调用 <see cref="CommitAsync"/> 删除。
    /// </summary>
    Task<OperationResult<IReadOnlyList<BatchMeasurements>>> DequeueAsync(int maxCount, CancellationToken ct = default);

    Task<OperationResult<IReadOnlyList<BatchMeasurements>>> DequeueAsync(int maxCount, string channel, CancellationToken ct = default)
        => DequeueAsync(maxCount, ct);

    /// <summary>确认转发成功，移除已出队的批次</summary>
    Task<OperationResult> CommitAsync(IReadOnlyList<Guid> batchIds, CancellationToken ct = default);

    /// <summary>
    /// 标记一次转发失败。内部累加重试计数，超过上限后自动丢弃。
    /// </summary>
    /// <param name="batchId">失败的批次 ID</param>
    /// <param name="reason">失败原因</param>
    /// <param name="ct">取消令牌</param>
    Task<OperationResult> MarkFailedAsync(Guid batchId, string reason, CancellationToken ct = default);

    /// <summary>获取死信队列中的条目（按入队时间升序，最多 maxCount 条）</summary>
    Task<OperationResult<IReadOnlyList<DeadLetterEntry>>> GetDeadLettersAsync(int maxCount, CancellationToken ct = default);

    /// <summary>将死信重新入队（重置重试计数，状态改回 Pending）</summary>
    Task<OperationResult> RetryDeadLetterAsync(Guid batchId, CancellationToken ct = default);

    /// <summary>丢弃死信（物理删除）</summary>
    Task<OperationResult> DiscardDeadLetterAsync(Guid batchId, CancellationToken ct = default);

    Task<OperationResult> PurgeDeadLettersAsync(DateTime before, CancellationToken ct = default);

    int Count { get; }

    /// <summary>异步获取当前待转发的批次数（不含死信）。async 路径请用本方法，避免同步查询阻塞</summary>
    Task<int> GetCountAsync(CancellationToken ct = default);

    /// <summary>
    /// 异步获取指定通道当前待转发的批次数。默认实现退化为全通道合计，
    /// 便于既有实现（测试替身）无需改动即可编译；支持通道隔离的实现应覆写。
    /// </summary>
    Task<int> GetCountAsync(string channel, CancellationToken ct = default)
        => GetCountAsync(ct);
}
