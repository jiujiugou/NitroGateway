using Polly;
using NitroGateway.Transport.MQTT;
using Xunit;

namespace NitroGateway.UnitTests.Transport;

/// <summary>
/// MQTT 重连的重试/退避已<b>外包给 Polly</b>（<see cref="MqttClientWrapper.BuildReconnectPipeline"/>）。
/// 这里验证外包后的策略契约，替代原先针对自写 <c>ComputeBackoffDelayMs</c> 的属性测试：
/// <list type="bullet">
/// <item>总尝试次数恒等于 <see cref="MqttConnectionOptions.MaxReconnectAttempts"/>（初始 1 次 + 重试 Max-1 次）。</item>
/// <item>每次退避延迟非负且封顶 <see cref="MqttConnectionOptions.ReconnectMaxIntervalMs"/>——
/// 原自写算法在 attempt≈23 时 int 溢出为负，Polly 的 <c>MaxDelay</c> 结构性消除该缺陷类。</item>
/// </list>
/// </summary>
public class MqttReconnectPolicyTests
{
    private static MqttConnectionOptions Options(int maxAttempts, int baseMs, int maxMs) => new()
    {
        Host = "localhost",
        Port = 1883,
        MaxReconnectAttempts = maxAttempts,
        ReconnectBackoffBaseMs = baseMs,
        ReconnectMaxIntervalMs = maxMs
    };

    [Fact]
    public async Task TotalAttempts_EqualsMaxReconnectAttempts()
    {
        var delays = new List<TimeSpan>();
        var pipeline = MqttClientWrapper.BuildReconnectPipeline(
            Options(maxAttempts: 5, baseMs: 1, maxMs: 4), onRetryDelay: delays.Add);

        var attempts = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await pipeline.ExecuteAsync(async _ =>
            {
                attempts++;
                await Task.Yield();
                throw new InvalidOperationException("connect failed");
            }));

        Assert.Equal(5, attempts);        // 初始 1 + 重试 4
        Assert.Equal(4, delays.Count);
    }

    [Fact]
    public async Task RetryDelay_IsBounded_EvenWithLargeAttemptCount()
    {
        // 用极小延迟（base=1ms, max=4ms）把大量重试跑快；关键断言是"延迟恒非负且不超上限"。
        var delays = new List<TimeSpan>();
        var pipeline = MqttClientWrapper.BuildReconnectPipeline(
            Options(maxAttempts: 40, baseMs: 1, maxMs: 4), onRetryDelay: delays.Add);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await pipeline.ExecuteAsync(async _ =>
            {
                await Task.Yield();
                throw new InvalidOperationException("connect failed");
            }));

        Assert.Equal(39, delays.Count);
        Assert.All(delays, d =>
        {
            Assert.True(d >= TimeSpan.Zero, $"退避延迟不得为负：{d}");
            Assert.True(d <= TimeSpan.FromMilliseconds(4), $"退避延迟不得超过上限：{d}");
        });
    }

    [Fact]
    public async Task SingleAttempt_WhenMaxIsOne_HasNoRetry()
    {
        var delays = new List<TimeSpan>();
        var pipeline = MqttClientWrapper.BuildReconnectPipeline(
            Options(maxAttempts: 1, baseMs: 1, maxMs: 4), onRetryDelay: delays.Add);

        var attempts = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await pipeline.ExecuteAsync(async _ =>
            {
                attempts++;
                await Task.Yield();
                throw new InvalidOperationException("connect failed");
            }));

        Assert.Equal(1, attempts);
        Assert.Empty(delays);
    }
}
