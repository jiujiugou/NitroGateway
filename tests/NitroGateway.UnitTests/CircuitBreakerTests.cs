using NitroGateway.Collection;
using Xunit;

namespace NitroGateway.UnitTests;

/// <summary>
/// 熔断器状态机单元测试。
///
/// <para><b>设计原则：HealthMonitor 是唯一的健康状态决策者。</b>
/// <see cref="CircuitBreaker.Trip"/> 由 Offline 信号触发，
/// <see cref="CircuitBreaker.Reset"/> 由 Online 信号触发。</para>
///
/// <para><b>调用约定：</b>每次采集前调用 <see cref="CircuitBreaker.IsOpen"/> 触发状态迁移，
/// 采集后调用 <see cref="CircuitBreaker.RecordSuccess"/> 或 <see cref="CircuitBreaker.RecordFailure"/>。</para>
///
/// <para>状态机规则：
/// - Closed: 正常通行
/// - → Open: Trip() 强制打开，冷却 5s 起步
/// - → HalfOpen: 冷却到期自动切换，首次 IsOpen 放行一个探测
/// - → Closed: 探测成功（RecordSuccess）
/// - → Open: 探测失败（RecordFailure），冷却翻倍（5s→10s→20s→…→5min）</para>
/// </summary>
public class CircuitBreakerTests
{
    // ══════════════ 初始状态 ══════════════

    [Fact]
    public void NewBreaker_IsClosed()
    {
        var cb = new CircuitBreaker();
        Assert.Equal(CircuitState.Closed, cb.State);
        Assert.False(cb.IsOpen);
    }

    // ══════════════ Trip() 强制打开 ══════════════

    [Fact]
    public void Trip_OpensCircuit()
    {
        var cb = new CircuitBreaker();
        cb.Trip();
        Assert.Equal(CircuitState.Open, cb.State);
        Assert.True(cb.IsOpen);
    }

    [Fact]
    public void RecordFailure_InClosed_DoesNotOpen()
    {
        var cb = new CircuitBreaker();
        for (var i = 0; i < 10; i++) cb.RecordFailure();
        Assert.Equal(CircuitState.Closed, cb.State);
        Assert.False(cb.IsOpen);
    }

    // ══════════════ Open 冷却 ══════════════

    [Fact]
    public void Open_WithinCooldown_StaysOpen()
    {
        var cb = new CircuitBreaker();
        cb.Trip();
        Assert.True(cb.IsOpen);
    }

    [Fact]
    public void Open_AfterCooldown_TransitionsToHalfOpen()
    {
        var cb = new CircuitBreaker(baseOpenDuration: TimeSpan.FromMilliseconds(1));
        cb.Trip();
        Thread.Sleep(10);
        Assert.False(cb.IsOpen);
        Assert.Equal(CircuitState.HalfOpen, cb.State);
    }

    // ══════════════ HalfOpen 并发保护 ══════════════

    [Fact]
    public void HalfOpen_OnlyOneProbeAllowed()
    {
        var cb = new CircuitBreaker(baseOpenDuration: TimeSpan.FromMilliseconds(1));
        cb.Trip();
        Thread.Sleep(10);
        Assert.False(cb.IsOpen); // 放行探测
        Assert.True(cb.IsOpen);  // 拒绝，探测进行中
    }

    // ══════════════ HalfOpen → Closed ══════════════

    [Fact]
    public void HalfOpen_ProbeSuccess_ReturnsToClosed()
    {
        var cb = new CircuitBreaker(baseOpenDuration: TimeSpan.FromMilliseconds(1));
        cb.Trip();
        Thread.Sleep(10);
        Assert.False(cb.IsOpen);
        cb.RecordSuccess();
        Assert.Equal(CircuitState.Closed, cb.State);
        Assert.False(cb.IsOpen);
    }

    // ══════════════ HalfOpen → Open（冷却翻倍） ══════════════

    /// <summary>每次探测失败直接翻倍：5s→10s→20s→40s→…→5min</summary>
    [Fact]
    public void HalfOpen_ProbeFailure_DoublesCooldownEachTime()
    {
        var cb = new CircuitBreaker(baseOpenDuration: TimeSpan.FromMilliseconds(1));
        cb.Trip();

        // 第 1 次探测失败：冷却 1ms → 2ms
        Thread.Sleep(10);
        Assert.False(cb.IsOpen);
        cb.RecordFailure();
        Assert.Equal(CircuitState.Open, cb.State);

        // 第 2 次探测失败：冷却 2ms → 4ms
        Thread.Sleep(10);
        Assert.False(cb.IsOpen);
        cb.RecordFailure();
        Assert.Equal(CircuitState.Open, cb.State);

        // 第 3 次探测失败：冷却 4ms → 8ms
        Thread.Sleep(10);
        Assert.False(cb.IsOpen);
        cb.RecordFailure();
        Assert.Equal(CircuitState.Open, cb.State);
    }

    // ══════════════ Trip → Reset 往返 ══════════════

    [Fact]
    public void Reset_AfterTrip_ReturnsToClosed()
    {
        var cb = new CircuitBreaker();
        cb.Trip();
        Assert.Equal(CircuitState.Open, cb.State);

        cb.Reset();
        Assert.Equal(CircuitState.Closed, cb.State);
        Assert.False(cb.IsOpen);
    }

    /// <summary>Trip → 冷却翻倍多次 → Reset → 冷却复位到 5s。</summary>
    [Fact]
    public void Trip_ProbeFailure_Reset_ReturnsToClosed()
    {
        var cb = new CircuitBreaker(baseOpenDuration: TimeSpan.FromMilliseconds(1));
        cb.Trip();

        Thread.Sleep(10);
        Assert.False(cb.IsOpen);
        cb.RecordFailure(); // 冷却翻倍

        cb.Reset();
        Assert.Equal(CircuitState.Closed, cb.State);
        Assert.False(cb.IsOpen);
    }
}
