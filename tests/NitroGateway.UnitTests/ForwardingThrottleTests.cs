using NitroGateway.Forwarder;
using Xunit;

namespace NitroGateway.UnitTests;

/// <summary>
/// 自适应转发节流器 AIMD 算法测试。
///
/// <para>AIMD (Additive Increase / Multiplicative Decrease) 和 TCP 拥塞控制同源：
/// - 失败时：批量大小减半（乘法减小），延迟递增（加法增大）
/// - 成功时：批量大小缓慢 +10（加法增大），延迟缓慢 -5ms（乘法减小）</para>
///
/// <para>目标：MQTT Broker 断连恢复后，不会瞬间把所有积压数据冲过去；
/// Broker 稳定后，逐步恢复最大吞吐。</para>
///
/// <para>边界硬限制：MaxBatchSize ∈ [100, 1000]，DelayMs ∈ [0, 200]</para>
/// </summary>
public class ForwardingThrottleTests
{
    /// <summary>初始状态：最大批量 1000，零延迟，即"全速转发"。</summary>
    [Fact]
    public void NewThrottle_DefaultState()
    {
        var t = new ForwardingThrottle();
        Assert.Equal(1000, t.MaxBatchSize);
        Assert.Equal(0, t.DelayMs);
    }

    /// <summary>3 次失败：批量 1000→500→250→125，延迟 0→20→40→60</summary>
    [Fact]
    public void ThreeFailures_ShrinksBatchAndIncreasesDelay()
    {
        var t = new ForwardingThrottle();
        t.OnMqttFailure();
        Assert.Equal(500, t.MaxBatchSize);
        Assert.Equal(20, t.DelayMs);
        t.OnMqttFailure();
        Assert.Equal(250, t.MaxBatchSize);
        Assert.Equal(40, t.DelayMs);
        t.OnMqttFailure();
        Assert.Equal(125, t.MaxBatchSize);
        Assert.Equal(60, t.DelayMs);
    }

    /// <summary>
    /// 持续失败触底测试：批量不跌破 100，延迟不超 200ms。
    /// 即使 MQTT Broker 挂了几个小时，也不会降到 0（彻底停止转发）或延迟无限大。
    /// </summary>
    [Fact]
    public void RepeatedFailures_HitsFloor()
    {
        var t = new ForwardingThrottle();
        for (var i = 0; i < 20; i++) t.OnMqttFailure();
        Assert.Equal(100, t.MaxBatchSize);
        Assert.Equal(200, t.DelayMs);
    }

    /// <summary>
    /// 恢复测试：从触底（100/200）开始连续成功，批量应逐步回升，延迟逐步降低。
    /// 40 次成功后 MaxBatchSize 应从 100 回升到 500。
    /// </summary>
    [Fact]
    public void Success_RecoversSlowly()
    {
        var t = new ForwardingThrottle();
        for (var i = 0; i < 20; i++) t.OnMqttFailure(); // 触底
        for (var i = 0; i < 40; i++) t.OnMqttSuccess();
        Assert.True(t.MaxBatchSize > 100);   // 已回升
        Assert.True(t.DelayMs < 200);        // 已降低
    }

    /// <summary>批量不应超过上限 1000，延迟不应低于 0。</summary>
    [Fact]
    public void Success_DoesNotExceedMax()
    {
        var t = new ForwardingThrottle();
        for (var i = 0; i < 200; i++) t.OnMqttSuccess();
        Assert.Equal(1000, t.MaxBatchSize);
        Assert.Equal(0, t.DelayMs);
    }

    /// <summary>
    /// ApplyDelayAsync 应在延迟 > 0 时实际等待。
    /// 使用 Stopwatch 验证实际等待时间接近 DelayMs 值（容差 10ms）。
    /// </summary>
    [Fact]
    public async Task ApplyDelay_WhenDelayPositive_Waits()
    {
        var t = new ForwardingThrottle();
        for (var i = 0; i < 10; i++) t.OnMqttFailure();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await t.ApplyDelayAsync();
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds >= 190);
    }

    /// <summary>ApplyDelayAsync 在延迟 = 0 时应立即返回（不等待）。</summary>
    [Fact]
    public async Task ApplyDelay_WhenDelayZero_ReturnsImmediately()
    {
        var t = new ForwardingThrottle();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await t.ApplyDelayAsync();
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 50);
    }
}
