using NitroGateway.Primitives.Resilience;
using Polly;
using Xunit;

namespace NitroGateway.UnitTests.Primitives;

/// <summary>
/// 通用韧性管线工厂测试：验证"机制统一"后，各领域依赖的策略契约仍然成立——
/// 重试次数 = MaxRetryAttempts + 1、退避延迟被封顶、取消不重试；
/// 超时语义分两种：<c>Timeout</c> 是整条管线总预算（外层），<c>AttemptTimeout</c> 是每次尝试
/// 各自超时（重试内层，退避不计入）。迁移前协议/HTTP/MQTT 各自手配 Polly，本测试锁定工厂产出的等价行为。
/// </summary>
public class ResiliencePipelineFactoryTests
{
    [Fact]
    public async Task Build_RetriesUpToMaxAttempts()
    {
        var pipeline = ResiliencePipelineFactory.Build(
            new ResiliencePolicy { MaxRetryAttempts = 3, RetryDelay = TimeSpan.FromMilliseconds(1) });

        var attempts = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await pipeline.ExecuteAsync(async _ =>
            {
                attempts++;
                await Task.Yield();
                throw new InvalidOperationException("boom");
            }));

        Assert.Equal(4, attempts); // 初始 1 + 重试 3
    }

    [Fact]
    public async Task Build_NoRetry_WhenMaxAttemptsZero()
    {
        var pipeline = ResiliencePipelineFactory.Build(
            new ResiliencePolicy { MaxRetryAttempts = 0 });

        var attempts = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await pipeline.ExecuteAsync(async _ =>
            {
                attempts++;
                await Task.Yield();
                throw new InvalidOperationException("boom");
            }));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task Build_MaxDelay_CapsBackoffDelay()
    {
        // 回归"退避延迟必须有界"：基数为 10s、上限 5ms，指数退避必须被封顶。
        var delays = new List<TimeSpan>();
        var pipeline = ResiliencePipelineFactory.Build(
            new ResiliencePolicy
            {
                MaxRetryAttempts = 4,
                RetryDelay = TimeSpan.FromSeconds(10),
                BackoffType = DelayBackoffType.Exponential,
                MaxDelay = TimeSpan.FromMilliseconds(5)
            },
            onRetryDelay: delays.Add);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await pipeline.ExecuteAsync(async _ =>
            {
                await Task.Yield();
                throw new InvalidOperationException("boom");
            }));

        Assert.Equal(4, delays.Count);
        Assert.All(delays, d => Assert.InRange(d.TotalMilliseconds, 0, 5));
    }

    [Fact]
    public async Task Build_Timeout_FailsFast_AndIsNotRetriedByDeadline()
    {
        // 超时策略先于重试加入（与原 ReliableProtocolDriver 相同的顺序），因此超时是整条管线的
        // 截止时间：内层一旦超过 Timeout，整体即失败，不会在超时后再重试。
        var pipeline = ResiliencePipelineFactory.Build(
            new ResiliencePolicy
            {
                MaxRetryAttempts = 2,
                RetryDelay = TimeSpan.FromMilliseconds(1),
                Timeout = TimeSpan.FromMilliseconds(50)
            });

        var attempts = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await pipeline.ExecuteAsync(async ct =>
            {
                attempts++;
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }));
        sw.Stop();

        Assert.Equal(1, attempts);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"应在超时附近失败，实际 {sw.Elapsed}");
    }

    [Fact]
    public async Task Build_AttemptTimeout_IsPerAttempt_RetriesWithinTotalBudget()
    {
        // AttemptTimeout 位于重试内层：每次尝试各自超时后重试，而不是首次超时即整体截止。
        // 与 Build_Timeout_FailsFast_AndIsNotRetriedByDeadline（Timeout 总预算）形成对照。
        var pipeline = ResiliencePipelineFactory.Build(
            new ResiliencePolicy
            {
                MaxRetryAttempts = 2,
                RetryDelay = TimeSpan.FromMilliseconds(1),
                AttemptTimeout = TimeSpan.FromMilliseconds(50)
            });

        var attempts = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await pipeline.ExecuteAsync(async ct =>
            {
                attempts++;
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }));
        sw.Stop();

        Assert.Equal(3, attempts);   // 初始 1 + 重试 2（每次 50ms 超时后重试）
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"应在每次尝试超时附近完成，实际 {sw.Elapsed}");
    }

    [Fact]
    public async Task Build_Generic_RetriesOnResultPredicate()
    {
        var pipeline = ResiliencePipelineFactory.Build(
            new ResiliencePolicy { MaxRetryAttempts = 2, RetryDelay = TimeSpan.FromMilliseconds(1) },
            shouldHandle: new PredicateBuilder<string>().HandleResult(s => s == "bad"));

        var attempts = 0;
        var result = await pipeline.ExecuteAsync(async _ =>
        {
            attempts++;
            await Task.Yield();
            return "bad";
        });

        Assert.Equal("bad", result);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task Build_Cancellation_IsNotRetried()
    {
        var pipeline = ResiliencePipelineFactory.Build(
            new ResiliencePolicy { MaxRetryAttempts = 5, RetryDelay = TimeSpan.FromMilliseconds(1) });

        var attempts = 0;
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await pipeline.ExecuteAsync(async ct =>
            {
                attempts++;
                cts.Cancel();                    // 首次尝试期间取消
                ct.ThrowIfCancellationRequested();
                await Task.Yield();
            }, cts.Token));

        Assert.Equal(1, attempts);               // 取消不触发重试
    }
}
