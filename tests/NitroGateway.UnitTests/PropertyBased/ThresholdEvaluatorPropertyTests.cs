using FsCheck;
using FsCheck.Xunit;
using NitroGateway.Alarm.Domain;
using NitroGateway.Alarm.Evaluation;
using Xunit;

namespace NitroGateway.UnitTests.PropertyBased;

/// <summary>
/// 阈值比较器的属性测试。用整数参数派生有限 double，避开 NaN 对比较律的干扰。
/// </summary>
public class ThresholdEvaluatorPropertyTests
{
    private static AlarmRule Rule(string op, double threshold, double? upper = null) => new()
    {
        DeviceId = Guid.Empty,
        PointId = Guid.Empty,
        Operator = op,
        Threshold = threshold,
        ThresholdUpper = upper
    };

    /// <summary>性质：">" 与 "<=" 恰好互补（对任意值/阈值，二者必一真一假）。</summary>
    [Property]
    public void Strict_and_weak_are_complements(int vi, int ti)
    {
        double v = vi, t = ti;
        Assert.NotEqual(
            ThresholdEvaluator.Evaluate(v, Rule(">", t)),
            ThresholdEvaluator.Evaluate(v, Rule("<=", t)));
    }

    /// <summary>性质：">=" 与 "<" 恰好互补。</summary>
    [Property]
    public void Weak_and_strict_are_complements(int vi, int ti)
    {
        double v = vi, t = ti;
        Assert.NotEqual(
            ThresholdEvaluator.Evaluate(v, Rule(">=", t)),
            ThresholdEvaluator.Evaluate(v, Rule("<", t)));
    }

    /// <summary>性质：Between 是闭区间判定。</summary>
    [Property]
    public void Between_is_closed_interval(int vi, int ai, int bi)
    {
        double v = vi;
        var lower = Math.Min(ai, bi);
        var upper = Math.Max(ai, bi);

        Assert.Equal(
            v >= lower && v <= upper,
            ThresholdEvaluator.Evaluate(v, Rule("Between", lower, upper)));
    }

    /// <summary>性质：Between 缺上限时恒为 false。</summary>
    [Property]
    public void Between_without_upper_is_always_false(int vi, int ti)
    {
        Assert.False(ThresholdEvaluator.Evaluate(vi, Rule("Between", ti)));
    }

    /// <summary>性质：未知运算符恒为 false。</summary>
    [Property]
    public void Unknown_operator_is_always_false(int vi, int ti, string op)
    {
        op ??= "";
        if (op is ">" or ">=" or "<" or "<=" or "==" or "!=" or "Between")
            return;

        Assert.False(ThresholdEvaluator.Evaluate(vi, Rule(op, ti)));
    }
}
