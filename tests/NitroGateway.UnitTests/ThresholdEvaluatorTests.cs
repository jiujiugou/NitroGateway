using NitroGateway.Alarm.Domain;
using NitroGateway.Alarm.Evaluation;
using Xunit;

namespace NitroGateway.UnitTests;

/// <summary>
/// 阈值比较器单元测试。
///
/// <para>ThresholdEvaluator 是告警引擎的数学基础——它决定一个采集值是否"超限"。
/// 每个操作符的语义必须和数学定义严格一致，工业场景下误报（false positive）
/// 会触发不必要的告警，漏报（false negative）可能导致设备损坏。</para>
///
/// <para>测试覆盖全部 7 种操作符 + Between（区间）+ 未知操作符（容错）共 13 个用例，
/// 每个用例验证边界值和正常值。</para>
///
/// <para>依赖此模块的消费者：AlarmEvaluator → AlarmHostedService → MqttAlarmNotifier。
/// 如果这里的比较逻辑出错，整个告警链路都会产生错误结果。</para>
/// </summary>
public class ThresholdEvaluatorTests
{
    // ══════════════════════════════════════════════════
    //  >  严格大于
    // ══════════════════════════════════════════════════

    /// <summary>
    /// 大于号的正常情形：81 > 80 应触发告警。
    /// 这是工业告警最常见的场景——温度超过上限。
    /// </summary>
    [Fact]
    public void GreaterThan_ValueExceedsThreshold_ReturnsTrue()
    {
        var rule = MakeRule(">", 80);
        Assert.True(ThresholdEvaluator.Evaluate(81, rule));
    }

    /// <summary>
    /// 大于号的边界：80 > 80 不成立。
    /// 这是最容易出 bug 的地方——如果实现用 >= 代替 >，边界值会误触发。
    /// </summary>
    [Fact]
    public void GreaterThan_ValueEqualsThreshold_ReturnsFalse()
    {
        var rule = MakeRule(">", 80);
        Assert.False(ThresholdEvaluator.Evaluate(80, rule));
    }

    // ══════════════════════════════════════════════════
    //  >=  大于等于
    // ══════════════════════════════════════════════════

    /// <summary>
    /// 大于等于的边界：80 >= 80 成立。
    /// >= 和 > 的区别只在这一个边界值上，工业上 "温度>=80 告警" 意味着 80.0 也告警。
    /// </summary>
    [Fact]
    public void GreaterOrEqual_ValueEqualsThreshold_ReturnsTrue()
    {
        var rule = MakeRule(">=", 80);
        Assert.True(ThresholdEvaluator.Evaluate(80, rule));
    }

    // ══════════════════════════════════════════════════
    //  <  严格小于
    // ══════════════════════════════════════════════════

    /// <summary>小于号正常情形：79 < 80 成立</summary>
    [Fact]
    public void LessThan_ValueBelowThreshold_ReturnsTrue()
    {
        var rule = MakeRule("<", 80);
        Assert.True(ThresholdEvaluator.Evaluate(79, rule));
    }

    /// <summary>小于号边界：80 < 80 不成立</summary>
    [Fact]
    public void LessThan_ValueEqualsThreshold_ReturnsFalse()
    {
        var rule = MakeRule("<", 80);
        Assert.False(ThresholdEvaluator.Evaluate(80, rule));
    }

    // ══════════════════════════════════════════════════
    //  <=  小于等于
    // ══════════════════════════════════════════════════

    /// <summary>小于等于边界：80 <= 80 成立</summary>
    [Fact]
    public void LessOrEqual_ValueEqualsThreshold_ReturnsTrue()
    {
        var rule = MakeRule("<=", 80);
        Assert.True(ThresholdEvaluator.Evaluate(80, rule));
    }

    // ══════════════════════════════════════════════════
    //  ==  等于（浮点容差 0.0001）
    // ══════════════════════════════════════════════════

    /// <summary>
    /// 等于操作符测试。使用 Math.Abs(value - threshold) 比较，
    /// 浮点误差 0.0001 以内视为相等——避免 IEEE 754 精度导致的误判。
    /// 工业场景：检测某个离散状态值（如开关=1）或零位检测。
    /// </summary>
    [Fact]
    public void Equal_ExactMatch_ReturnsTrue()
    {
        var rule = MakeRule("==", 80);
        Assert.True(ThresholdEvaluator.Evaluate(80, rule));
    }

    // ══════════════════════════════════════════════════
    //  !=  不等于
    // ══════════════════════════════════════════════════

    /// <summary>不等于操作符。常用于检测状态变化或非零值。</summary>
    [Fact]
    public void NotEqual_DifferentValue_ReturnsTrue()
    {
        var rule = MakeRule("!=", 80);
        Assert.True(ThresholdEvaluator.Evaluate(79, rule));
    }

    // ══════════════════════════════════════════════════
    //  Between  区间 [Lower, Upper] 包含两端
    // ══════════════════════════════════════════════════

    /// <summary>Between 区间内：75 ∈ [70, 80] → 成立</summary>
    [Fact]
    public void Between_ValueInRange_ReturnsTrue()
    {
        var rule = MakeBetweenRule(70, 80);
        Assert.True(ThresholdEvaluator.Evaluate(75, rule));
    }

    /// <summary>Between 下边界：70 ∈ [70, 80] → 成立（包含下限）</summary>
    [Fact]
    public void Between_ValueAtLowerBound_ReturnsTrue()
    {
        var rule = MakeBetweenRule(70, 80);
        Assert.True(ThresholdEvaluator.Evaluate(70, rule));
    }

    /// <summary>Between 上边界：80 ∈ [70, 80] → 成立（包含上限）</summary>
    [Fact]
    public void Between_ValueAtUpperBound_ReturnsTrue()
    {
        var rule = MakeBetweenRule(70, 80);
        Assert.True(ThresholdEvaluator.Evaluate(80, rule));
    }

    /// <summary>Between 超上限：81 ∉ [70, 80] → 不成立</summary>
    [Fact]
    public void Between_ValueAboveRange_ReturnsFalse()
    {
        var rule = MakeBetweenRule(70, 80);
        Assert.False(ThresholdEvaluator.Evaluate(81, rule));
    }

    // ══════════════════════════════════════════════════
    //  容错 — 未知操作符
    // ══════════════════════════════════════════════════

    /// <summary>
    /// 未知操作符测试。数据库中的 Operator 字段可能被错误配置，
    /// 此时不应抛异常导致整个评估链路崩溃，而是静默返回 false。
    /// </summary>
    [Fact]
    public void UnknownOperator_ReturnsFalse_DoesNotThrow()
    {
        var rule = MakeRule("??", 80);
        var result = false;
        var ex = Record.Exception(() => result = ThresholdEvaluator.Evaluate(80, rule));
        Assert.Null(ex);       // 不抛异常
        Assert.False(result);  // 未知操作符 → 不触发告警（安全侧）
    }

    // ── 测试辅助方法 ──

    /// <summary>创建单阈值告警规则（用于 >, >=, <, <=, ==, !=）</summary>
    private static AlarmRule MakeRule(string op, double threshold) => new()
    {
        Id = Guid.NewGuid(),
        DeviceId = Guid.NewGuid(),
        PointId = Guid.NewGuid(),
        Operator = op,
        Threshold = threshold
    };

    /// <summary>创建 Between 区间告警规则</summary>
    private static AlarmRule MakeBetweenRule(double lower, double upper) => new()
    {
        Id = Guid.NewGuid(),
        DeviceId = Guid.NewGuid(),
        PointId = Guid.NewGuid(),
        Operator = "Between",
        Threshold = lower,
        ThresholdUpper = upper
    };
}
