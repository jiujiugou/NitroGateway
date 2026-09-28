using Microsoft.Extensions.Logging;
using Polly;

namespace NitroGateway.Primitives.Resilience;

/// <summary>
/// 韧性管线的**策略参数**（领域决定：谁重试、几次、退避多大、是否超时）。
/// 机制（如何构造 Polly 管线）由 <see cref="ResiliencePipelineFactory"/> 统一负责。
/// </summary>
public sealed record ResiliencePolicy
{
    /// <summary>最大重试次数；0 表示不重试（管线只执行一次）</summary>
    public int MaxRetryAttempts { get; init; }

    /// <summary>首次重试延迟（指数退避的起点）</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>退避类型</summary>
    public DelayBackoffType BackoffType { get; init; } = DelayBackoffType.Exponential;

    /// <summary>退避延迟上限；null 表示不封顶</summary>
    public TimeSpan? MaxDelay { get; init; }

    /// <summary>是否加入抖动（避免惊群）</summary>
    public bool UseJitter { get; init; }

    /// <summary>
    /// 整条管线的总预算（含重试与退避）；null 表示不加总超时。
    /// 位于重试外层：达到预算即整体失败，不再重试。
    /// </summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>
    /// 每次尝试的超时；位于重试内层，重试之间的退避等待不计入。
    /// null 表示不单独限制每次尝试（仅受 <see cref="Timeout"/> 总预算约束）。
    /// 与 <see cref="Timeout"/> 可同时存在：总预算在外、每次尝试在内。
    /// </summary>
    public TimeSpan? AttemptTimeout { get; init; }

    /// <summary>重试日志级别（各领域原语义不同：协议 Debug / HTTP Warning / MQTT Information）</summary>
    public LogLevel RetryLogLevel { get; init; } = LogLevel.Debug;

    /// <summary>操作名，仅用于重试日志</summary>
    public string? OperationName { get; init; }
}
