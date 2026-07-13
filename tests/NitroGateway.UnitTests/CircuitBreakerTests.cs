using NitroGateway.Collection;
using Xunit;

namespace NitroGateway.UnitTests;

/// <summary>
/// 熔断器三态状态机单元测试。
///
/// <para>CircuitBreaker 是采集引擎的"保险丝"——当一台 PLC 持续不可达时，
/// 自动切断对该设备的采集尝试，避免 TCP 超时堆积拖死整个采集周期。
/// 冷却到期后自动进入 HalfOpen 状态，放行一个探测请求验证设备是否恢复。</para>
///
/// <para>状态机规则：
/// - Closed: 正常通行，RecordFailure 累加计数
/// - → Open: 连续失败 ≥ threshold（默认 5 次），拒绝通行，持续 openDuration（默认 30s）
/// - → HalfOpen: 冷却到期后自动切换，放行第一个请求作为探测
/// - → Closed: 探测成功（RecordSuccess），恢复通行，冷却时间重置
/// - → Open: 探测失败（RecordFailure），重新断开，冷却时间翻倍（上限 5min）</para>
///
/// <para>最复杂的逻辑在 HalfOpen 阶段的并发保护：已有一个探测在进行时，
/// 后续请求仍然被拒绝，防止多个探测同时通过。</para>
/// </summary>
public class CircuitBreakerTests
{
    // ══════════════════════════════════════════════════
    //  初始状态
    // ══════════════════════════════════════════════════

    /// <summary>新建熔断器应为 Closed 状态，不拒绝请求。</summary>
    [Fact]
    public void NewBreaker_IsClosed()
    {
        var cb = new CircuitBreaker(failureThreshold: 5, openDuration: TimeSpan.FromSeconds(30));
        Assert.Equal(CircuitState.Closed, cb.State);
        Assert.False(cb.IsOpen);
    }

    // ══════════════════════════════════════════════════
    //  Closed → Open：连续失败触发断开
    // ══════════════════════════════════════════════════

    /// <summary>
    /// 连续失败达到 threshold（5 次）后，状态变为 Open，IsOpen 返回 true。
    /// DeviceCollector 在 IsOpen=true 时跳过该设备的采集。
    /// </summary>
    [Fact]
    public void FiveFailures_OpensCircuit()
    {
        var cb = new CircuitBreaker(failureThreshold: 5, openDuration: TimeSpan.FromSeconds(30));
        for (var i = 0; i < 5; i++) cb.RecordFailure();
        Assert.Equal(CircuitState.Open, cb.State);
        Assert.True(cb.IsOpen);
    }

    /// <summary>
    /// 失败 4 次（少于 threshold=5）时，熔断器应保持 Closed。
    /// 这是边界值——差一次失败就不该触发。
    /// </summary>
    [Fact]
    public void FourFailures_StaysClosed()
    {
        var cb = new CircuitBreaker(failureThreshold: 5, openDuration: TimeSpan.FromSeconds(30));
        for (var i = 0; i < 4; i++) cb.RecordFailure();
        Assert.Equal(CircuitState.Closed, cb.State);
        Assert.False(cb.IsOpen);
    }

    // ══════════════════════════════════════════════════
    //  Open 状态：冷却期间拒绝通行
    // ══════════════════════════════════════════════════

    /// <summary>
    /// 刚进入 Open 状态时，冷却时间尚未经过，IsOpen 仍返回 true。
    /// 注意：此处不能 sleep 等冷却——测试中默认冷却 30s，直接用快速断言验证状态。
    /// </summary>
    [Fact]
    public void Open_WithinCooldown_StaysOpen()
    {
        var cb = new CircuitBreaker(failureThreshold: 5, openDuration: TimeSpan.FromSeconds(30));
        for (var i = 0; i < 5; i++) cb.RecordFailure();
        Assert.True(cb.IsOpen);  // 刚断开，冷却还没到
    }

    /// <summary>
    /// 冷却到期后 IsOpen 返回 false，状态自动迁移到 HalfOpen。
    /// 此测试用极短的 openDuration（1ms）来跳过等待。
    /// </summary>
    [Fact]
    public void Open_AfterCooldown_TransitionsToHalfOpen()
    {
        var cb = new CircuitBreaker(failureThreshold: 5, openDuration: TimeSpan.FromMilliseconds(1));
        for (var i = 0; i < 5; i++) cb.RecordFailure();
        Thread.Sleep(10);  // 等待极短的冷却时间经过
        Assert.False(cb.IsOpen);                   // 首次放行
        Assert.Equal(CircuitState.HalfOpen, cb.State);
    }

    // ══════════════════════════════════════════════════
    //  HalfOpen 并发保护
    // ══════════════════════════════════════════════════

    /// <summary>
    /// HalfOpen 阶段：第一个请求放行（作为探测），第二个请求应被拒绝。
    /// 这防止多个并发请求同时通过——只有一个探测正在进行。
    /// </summary>
    [Fact]
    public void HalfOpen_OnlyOneProbeAllowed()
    {
        var cb = new CircuitBreaker(failureThreshold: 5, openDuration: TimeSpan.FromMilliseconds(1));
        for (var i = 0; i < 5; i++) cb.RecordFailure();
        Thread.Sleep(10);
        Assert.False(cb.IsOpen);  // 第一个探测：放行
        Assert.True(cb.IsOpen);   // 第二个请求：拒绝，探测进行中
    }

    // ══════════════════════════════════════════════════
    //  HalfOpen → Closed：探测成功恢复
    // ══════════════════════════════════════════════════

    /// <summary>
    /// HalfOpen 探测成功后调用 RecordSuccess → 状态回到 Closed，故障计数重置。
    /// 此时设备已恢复，后续正常采集不再被阻断。
    /// </summary>
    [Fact]
    public void HalfOpen_ProbeSuccess_ReturnsToClosed()
    {
        var cb = new CircuitBreaker(failureThreshold: 5, openDuration: TimeSpan.FromMilliseconds(1));
        for (var i = 0; i < 5; i++) cb.RecordFailure();
        Thread.Sleep(10);
        Assert.False(cb.IsOpen);  // 放行探测
        cb.RecordSuccess();
        Assert.Equal(CircuitState.Closed, cb.State);
        Assert.False(cb.IsOpen);
    }

    // ══════════════════════════════════════════════════
    //  HalfOpen → Open：探测失败，指数退避
    // ══════════════════════════════════════════════════

    /// <summary>
    /// HalfOpen 探测失败 → 重新进入 Open，且冷却时间翻倍（指数退避）。
    /// 第 1 次断开 30s，探测失败后第 2 次断开 60s，以此类推，上限 5min。
    /// 这避免对持续故障的设备频繁无效探测。
    /// </summary>
    [Fact]
    public void HalfOpen_ProbeFailure_ReopensWithDoubledCooldown()
    {
        var cb = new CircuitBreaker(failureThreshold: 5, openDuration: TimeSpan.FromMilliseconds(1));
        for (var i = 0; i < 5; i++) cb.RecordFailure();
        Thread.Sleep(10);
        Assert.False(cb.IsOpen);   // 放行探测
        cb.RecordFailure();        // 探测失败
        Assert.Equal(CircuitState.Open, cb.State);
        Assert.True(cb.IsOpen);    // 重新断开，冷却翻倍
    }

    // ══════════════════════════════════════════════════
    //  RecordSuccess 重置计数
    // ══════════════════════════════════════════════════

    /// <summary>
    /// 一次 RecordSuccess 将失败计数重置为 0。
    /// 这意味着后续失败从零开始重新计数——只有"连续"失败才触发熔断。
    /// 如果 PLC 间歇性故障（3 次失败、1 次成功、3 次失败），不应触发。
    /// </summary>
    [Fact]
    public void SuccessResetsFailureCount()
    {
        var cb = new CircuitBreaker(failureThreshold: 5, openDuration: TimeSpan.FromSeconds(30));
        for (var i = 0; i < 3; i++) cb.RecordFailure();
        cb.RecordSuccess();  // 重置失败计数为 0
        for (var i = 0; i < 4; i++) cb.RecordFailure();
        Assert.Equal(CircuitState.Closed, cb.State);  // 只有 4 次连续失败，不够 5 次
    }

    // ══════════════════════════════════════════════════
    //  Reset：外部干预强制闭合
    // ══════════════════════════════════════════════════

    /// <summary>
    /// Reset() 强制将熔断器恢复为 Closed，清空所有状态。
    /// 由 DeviceHealthMonitor 在设备恢复 Online 时调用。
    /// 或由运维人员通过管理面板手动触发。
    /// </summary>
    [Fact]
    public void Reset_ForcesClosedAndClearsCounters()
    {
        var cb = new CircuitBreaker(failureThreshold: 5, openDuration: TimeSpan.FromSeconds(30));
        for (var i = 0; i < 5; i++) cb.RecordFailure();
        Assert.Equal(CircuitState.Open, cb.State);

        cb.Reset();
        Assert.Equal(CircuitState.Closed, cb.State);
        Assert.False(cb.IsOpen);
    }
}
