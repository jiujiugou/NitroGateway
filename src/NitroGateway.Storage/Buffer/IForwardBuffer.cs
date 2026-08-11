using NitroGateway.Domain.Measurements;
using NitroGateway.Shared;

namespace NitroGateway.Storage.Buffer;

/// <summary>
/// 转发缓冲接口。FIFO 队列，断电不丢。
/// Collection 写入 → Forwarder 取出 → 确认成功 → 提交删除。
/// 转发失败的消息经过重试后移入死信队列 (DeadLetter)。
/// </summary>
public interface IForwardBuffer
{
    /// <summary>MQTT 转发通道名（ADR-011 多通道；默认通道，旧行为）</summary>
    public const string MqttChannel = "mqtt";

    /// <summary>HTTP 转发通道名（ADR-011；由 HttpForwarderEngine 消费）</summary>
    public const string HttpChannel = "http";

    /// <summary>入队一批待转发数据。不应阻塞调用方</summary>
    Task<OperationResult> EnqueueAsync(BatchMeasurements batch, CancellationToken ct = default);

    /// <summary>
    /// 入队一批待转发数据到指定通道（ADR-011）。默认实现委托 <see cref="EnqueueAsync(BatchMeasurements, CancellationToken)"/>
    /// （mqtt 通道），实现类可覆盖以支持通道隔离；接口只增不删，旧实现零改动。
    /// </summary>
    /// <param name="batch">待转发批次</param>
    /// <param name="channel">通道名（<see cref="MqttChannel"/> / <see cref="HttpChannel"/>）</param>
    /// <param name="ct">取消令牌</param>
    Task<OperationResult> EnqueueAsync(BatchMeasurements batch, string channel, CancellationToken ct = default)
        => EnqueueAsync(batch, ct);

    /// <summary>
    /// 出队最多 maxCount 批数据，不移除。
    /// 只返回 Pending 且未超过重试上限的批次。Forwarder 成功转发后调用 <see cref="CommitAsync"/> 删除。
    /// </summary>
    Task<OperationResult<IReadOnlyList<BatchMeasurements>>> DequeueAsync(int maxCount, CancellationToken ct = default);

    /// <summary>
    /// 出队指定通道最多 maxCount 批数据（ADR-011）。默认实现委托
    /// <see cref="DequeueAsync(int, CancellationToken)"/>（mqtt 通道），实现类可覆盖以支持通道隔离。
    /// </summary>
    /// <param name="maxCount">最大批次数</param>
    /// <param name="channel">通道名（<see cref="MqttChannel"/> / <see cref="HttpChannel"/>）</param>
    /// <param name="ct">取消令牌</param>
    Task<OperationResult<IReadOnlyList<BatchMeasurements>>> DequeueAsync(int maxCount, string channel, CancellationToken ct = default)
        => DequeueAsync(maxCount, ct);

    /// <summary>确认转发成功，移除已出队的批次</summary>
    Task<OperationResult> CommitAsync(IReadOnlyList<Guid> batchIds, CancellationToken ct = default);

    /// <summary>
    /// 标记一次转发失败。内部累加重试计数，超过上限后自动移入死信队列。
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

    /// <summary>
    /// 清理指定入队时间之前的死信（物理删除）。ADR-018 P2-3：防止坏消息持续累积死信表，
    /// 与 measurements 保留清理对称；实现应分批删除避免长时间持锁。
    /// </summary>
    /// <param name="before">清理边界（含边界之前的所有死信）</param>
    /// <param name="ct">取消令牌</param>
    Task<OperationResult> PurgeDeadLettersAsync(DateTime before, CancellationToken ct = default);

    /// <summary>
    /// 当前队列中待转发的批次数（不含死信）。
    /// ADR-021 P3-1：同步查询可能阻塞（实现为同步查库），热路径与异步调用请使用 <see cref="GetCountAsync"/>。
    /// </summary>
    int Count { get; }

    /// <summary>异步获取当前待转发的批次数（不含死信）。async 路径请用本方法，避免同步查询阻塞</summary>
    Task<int> GetCountAsync(CancellationToken ct = default);
}
