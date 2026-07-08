namespace NitroGateway.Alarm.Evaluation;

/// <summary>
/// 阈值比较器。将比较逻辑抽象为一处 switch，支持未来扩展 Between、InRange、变化率等。
/// Evaluator 永远不用因增加运算符而修改。
/// </summary>
public static class ThresholdEvaluator
{
    /// <summary>判断 value 是否满足 rule 的阈值条件</summary>
    public static bool Evaluate(double value, Domain.AlarmRule rule)
    {
        return rule.Operator switch
        {
            ">"  => value > rule.Threshold,
            ">=" => value >= rule.Threshold,
            "<"  => value < rule.Threshold,
            "<=" => value <= rule.Threshold,
            "==" => Math.Abs(value - rule.Threshold) < 0.0001,
            "!=" => Math.Abs(value - rule.Threshold) > 0.0001,
            "Between" => rule.ThresholdUpper.HasValue
                && value >= rule.Threshold
                && value <= rule.ThresholdUpper.Value,
            _ => false
        };
    }
}
