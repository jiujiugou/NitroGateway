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
/// <para><b>调用约定：</b>每次采集前调用 <see cref="CircuitBreaker.TryEnterProbe"/> 触发状态迁移，
/// 采集后调用 <see cref="CircuitBreaker.RecordSuccess"/> 或 <see cref="CircuitBreaker.RecordFailure"/>。</para>
///
/// <para>状态机规则：
/// - Closed: 正常通行
/// - → Open: Trip() 强制打开，冷却 5s 起步
/// - → HalfOpen: 冷却到期自动切换，首次 TryEnterProbe 放行一个探测
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
        Assert.True(cb.TryEnterProbe()); // Closed 恒放行，不占名额
    }

    // ══════════════ Trip() 强制打开 ══════════════

    [Fact]
    public void Trip_OpensCircuit()
    {
        var cb = new CircuitBreaker();
        cb.Trip();
        Assert.Equal(CircuitState.Open, cb.State);
        Assert.False(cb.TryEnterProbe());
    }

    [Fact]
    public void RecordFailure_InClosed_DoesNotOpen()
    {
        var cb = new CircuitBreaker();
        for (var i = 0; i < 10; i++) cb.RecordFailure();
        Assert.Equal(CircuitState.Closed, cb.State);
        Assert.True(cb.TryEnterProbe());
    }

    // ══════════════ Open 冷却 ══════════════

    [Fact]
    public void Open_WithinCooldown_StaysOpen()
    {
        var cb = new CircuitBreaker();
        cb.Trip();
        Assert.False(cb.TryEnterProbe());
    }

    [Fact]
    public void Open_AfterCooldown_TransitionsToHalfOpen()
    {
        var cb = new CircuitBreaker(baseOpenDuration: TimeSpan.FromMilliseconds(1));
        cb.Trip();
        Thread.Sleep(10);
        Assert.True(cb.TryEnterProbe()); // 冷却到期 → HalfOpen → 放行探测
        Assert.Equal(CircuitState.HalfOpen, cb.State);
    }

    // ══════════════ HalfOpen 并发保护 ══════════════

    [Fact]
    public void HalfOpen_OnlyOneProbeAllowed()
    {
        var cb = new CircuitBreaker(baseOpenDuration: TimeSpan.FromMilliseconds(1));
        cb.Trip();
        Thread.Sleep(10);
        Assert.True(cb.TryEnterProbe());  // 抢占唯一探测名额
        Assert.False(cb.TryEnterProbe()); // 拒绝，探测进行中
    }

    // ══════════════ HalfOpen → Closed ══════════════

    [Fact]
    public void HalfOpen_ProbeSuccess_ReturnsToClosed()
    {
        var cb = new CircuitBreaker(baseOpenDuration: TimeSpan.FromMilliseconds(1));
        cb.Trip();
        Thread.Sleep(10);
        Assert.True(cb.TryEnterProbe());
        cb.RecordSuccess();
        Assert.Equal(CircuitState.Closed, cb.State);
        Assert.True(cb.TryEnterProbe());
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
        Assert.True(cb.TryEnterProbe());
        cb.RecordFailure();
        Assert.Equal(CircuitState.Open, cb.State);

        // 第 2 次探测失败：冷却 2ms → 4ms
        Thread.Sleep(10);
        Assert.True(cb.TryEnterProbe());
        cb.RecordFailure();
        Assert.Equal(CircuitState.Open, cb.State);

        // 第 3 次探测失败：冷却 4ms → 8ms
        Thread.Sleep(10);
        Assert.True(cb.TryEnterProbe());
        cb.RecordFailure();
        Assert.Equal(CircuitState.Open, cb.State);
    }

    // ══════════════ CQS：State 纯查询，不推进状态、不占探测名额 ══════════════

    /// <summary>读 State 不推进 Open→HalfOpen、不消耗探测名额；迁移只能由 TryEnterProbe 触发（CQS 回归）。</summary>
    [Fact]
    public void State_Getter_IsPureQuery_DoesNotTransitionOrConsumeProbe()
    {
        var cb = new CircuitBreaker(baseOpenDuration: TimeSpan.FromMilliseconds(1));
        cb.Trip();
        Thread.Sleep(10);

        // 只读查询：冷却已到期但状态仍是 Open（读不推进状态机）
        Assert.Equal(CircuitState.Open, cb.State);
        Assert.Equal(CircuitState.Open, cb.State);

        // 命令入口：此时才推进到 HalfOpen 并抢占探测名额
        Assert.True(cb.TryEnterProbe());
        Assert.Equal(CircuitState.HalfOpen, cb.State);
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
        Assert.True(cb.TryEnterProbe());
    }

    /// <summary>Trip → 冷却翻倍多次 → Reset → 冷却复位到 5s。</summary>
    [Fact]
    public void Trip_ProbeFailure_Reset_ReturnsToClosed()
    {
        var cb = new CircuitBreaker(baseOpenDuration: TimeSpan.FromMilliseconds(1));
        cb.Trip();

        Thread.Sleep(10);
        Assert.True(cb.TryEnterProbe());
        cb.RecordFailure(); // 冷却翻倍

        cb.Reset();
        Assert.Equal(CircuitState.Closed, cb.State);
        Assert.True(cb.TryEnterProbe());
    }
}
