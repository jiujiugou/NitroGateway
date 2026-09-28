using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;

namespace NitroGateway.Primitives.Resilience;

/// <summary>
/// 通用韧性管线工厂：把"重试 / 退避 / 超时"的**机制**收敛到一处，
/// 各领域只提供 <see cref="ResiliencePolicy"/> 参数（策略）。
/// <para>非泛型 <see cref="Build(ResiliencePolicy, ILogger?, Action{TimeSpan}?)"/>：异常驱动重试（如协议读、MQTT 建连）。</para>
/// <para>泛型 <see cref="Build{TResult}"/>：结果/异常驱动重试（如 HTTP 5xx）。</para>
/// <para>Polly 默认不对 <see cref="OperationCanceledException"/> 重试；调用方取消经 token 传播后直接抛出。</para>
/// </summary>
public static class ResiliencePipelineFactory
{
    /// <summary>构建非泛型管线（异常驱动重试）。</summary>
    /// <param name="policy">策略参数</param>
    /// <param name="logger">重试日志（可空）</param>
    /// <param name="onRetryDelay">每次重试的实际延迟回调（仅测试用于验证退避/封顶）</param>
    public static ResiliencePipeline Build(
        ResiliencePolicy policy,
        ILogger? logger = null,
        Action<TimeSpan>? onRetryDelay = null)
    {
        var builder = new ResiliencePipelineBuilder();

        // 顺序即嵌套：先加在外层。总预算在外 → 重试居中 → 每次尝试在内。
        if (policy.Timeout is { } timeout)
            builder.AddTimeout(timeout);            // 外层：整条管线总预算（含重试与退避）

        if (policy.MaxRetryAttempts > 0)
            builder.AddRetry(CreateRetryOptions(policy, logger, onRetryDelay)); // 中层：重试

        if (policy.AttemptTimeout is { } attemptTimeout)
            builder.AddTimeout(attemptTimeout);     // 内层：每次尝试超时（退避不计入）

        return builder.Build();
    }

    /// <summary>
    /// 构建泛型管线（结果/异常驱动重试）。
    /// <paramref name="shouldHandle"/> 为空时默认重试"除取消外的所有异常"；
    /// 需要结果判定（如 HTTP 5xx）时由调用方传入 <see cref="PredicateBuilder{TResult}"/>。
    /// </summary>
    public static ResiliencePipeline<TResult> Build<TResult>(
        ResiliencePolicy policy,
        PredicateBuilder<TResult>? shouldHandle = null,
        ILogger? logger = null)
    {
        var builder = new ResiliencePipelineBuilder<TResult>();

        // 顺序即嵌套：总预算在外 → 重试居中 → 每次尝试在内。
        if (policy.Timeout is { } timeout)
            builder.AddTimeout(timeout);            // 外层：整条管线总预算

        if (policy.MaxRetryAttempts > 0)
            builder.AddRetry(CreateRetryOptions(policy, shouldHandle, logger)); // 中层：重试

        if (policy.AttemptTimeout is { } attemptTimeout)
            builder.AddTimeout(attemptTimeout);     // 内层：每次尝试超时（退避不计入）

        return builder.Build();
    }

    private static RetryStrategyOptions CreateRetryOptions(
        ResiliencePolicy policy, ILogger? logger, Action<TimeSpan>? onRetryDelay)
    {
        var options = new RetryStrategyOptions
        {
            MaxRetryAttempts = policy.MaxRetryAttempts,
            Delay = policy.RetryDelay,
            BackoffType = policy.BackoffType,
            UseJitter = policy.UseJitter,
            OnRetry = args =>
            {
                onRetryDelay?.Invoke(args.RetryDelay);
                LogRetry(logger, policy, args.AttemptNumber, args.RetryDelay, args.Outcome.Exception);
                return default;
            }
        };

        // Polly 对 MaxDelay 有 [0, 1 天] 的校验区间，未提供时保持其默认值（不显式设 MaxValue）。
        if (policy.MaxDelay is { } maxDelay)
            options.MaxDelay = maxDelay;

        return options;
    }

    private static RetryStrategyOptions<TResult> CreateRetryOptions<TResult>(
        ResiliencePolicy policy, PredicateBuilder<TResult>? shouldHandle, ILogger? logger)
    {
        var options = new RetryStrategyOptions<TResult>
        {
            MaxRetryAttempts = policy.MaxRetryAttempts,
            Delay = policy.RetryDelay,
            BackoffType = policy.BackoffType,
            UseJitter = policy.UseJitter,
            ShouldHandle = shouldHandle
                ?? new PredicateBuilder<TResult>().Handle<Exception>(e => e is not OperationCanceledException),
            OnRetry = args =>
            {
                LogRetry(logger, policy, args.AttemptNumber, args.RetryDelay, args.Outcome.Exception);
                return default;
            }
        };

        if (policy.MaxDelay is { } maxDelay)
            options.MaxDelay = maxDelay;

        return options;
    }

    private static void LogRetry(ILogger? logger, ResiliencePolicy policy, int attemptNumber, TimeSpan delay, Exception? error)
    {
        if (logger is null || !logger.IsEnabled(policy.RetryLogLevel))
            return;

        logger.Log(policy.RetryLogLevel,
            "{Operation} 第 {Attempt}/{Max} 次重试，等待 {Delay}ms: {Error}",
            policy.OperationName ?? "操作",
            attemptNumber + 1,
            policy.MaxRetryAttempts,
            delay.TotalMilliseconds,
            error?.Message ?? "无异常（结果判定触发）");
    }
}
