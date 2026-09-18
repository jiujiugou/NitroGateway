using FsCheck.Xunit;
using NitroGateway.Alarm.Domain;
using NitroGateway.Alarm.Evaluation;

namespace NitroGateway.UnitTests.PropertyBased;

/// <summary>告警评估器（Duration 去抖 + 去重）的属性测试。</summary>
public class AlarmEvaluatorPropertyTests
{
    private static int Pos(int seed, int mod) => (int)(Math.Abs((long)seed) % mod);

    private static AlarmRule Rule(int durationSeconds, string op = ">", double threshold = 50, double? upper = null)
        => new()
        {
            Id = Guid.NewGuid(),
            DeviceId = Guid.NewGuid(),
            PointId = Guid.NewGuid(),
            Operator = op,
            Threshold = threshold,
            ThresholdUpper = upper,
            DurationSeconds = durationSeconds,
            Severity = AlarmSeverity.Warning,
            Enabled = true
        };

    /// <summary>性质：Duration 去抖——持续超限时，恰好在第 DurationSeconds 秒触发 Active。</summary>
    [Property]
    public bool Duration_gates_activation(int durationSeed)
    {
        var duration = Pos(durationSeed, 6);   // 0..5 秒
        var rule = Rule(duration);
        var evaluator = new AlarmEvaluator();
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        int? firstActiveSecond = null;
        for (var s = 0; s <= duration + 1; s++)
        {
            var results = evaluator.Evaluate(rule.DeviceId, rule.PointId, 100.0, [rule], t0.AddSeconds(s));
            if (results.Any(r => r.NewState == AlarmState.Active))
            {
                firstActiveSecond = s;
                break;
            }
        }

        return firstActiveSecond == duration;
    }

    /// <summary>性质：在恢复（Resolved）之前，同一规则至多产生一次 Active；Resolved 必在 Active 之后。</summary>
    [Property]
    public bool Active_is_unique_until_resolved(int seed)
    {
        var rnd = new System.Random(seed);
        var rule = Rule(Pos(rnd.Next(), 4));
        var evaluator = new AlarmEvaluator();
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var activeOpen = false;

        for (var i = 0; i < 40; i++)
        {
            var exceeded = rnd.Next(2) == 0;
            var results = evaluator.Evaluate(rule.DeviceId, rule.PointId, exceeded ? 100.0 : 0.0, [rule], now);

            foreach (var r in results)
            {
                if (r.NewState == AlarmState.Active)
                {
                    if (activeOpen) return false;   // 未恢复就再次 Active
                    activeOpen = true;
                }
                else if (r.NewState == AlarmState.Resolved)
                {
                    if (!activeOpen) return false;  // 未 Active 就 Resolved
                    activeOpen = false;
                }
            }

            now = now.AddSeconds(rnd.Next(0, 4));
        }

        return true;
    }

    /// <summary>性质：对任意规则/值，Evaluate 都不抛异常。</summary>
    [Property]
    public bool Evaluate_never_throws(int durationSeed, int opSeed, int valueSeed)
    {
        var ops = new[] { ">", ">=", "<", "<=", "==", "!=", "Between", "??" };
        var rule = Rule(Pos(durationSeed, 5), ops[Pos(opSeed, ops.Length)], threshold: 50, upper: 80);
        var evaluator = new AlarmEvaluator();

        try
        {
            evaluator.Evaluate(rule.DeviceId, rule.PointId, Pos(valueSeed, 200), [rule], DateTime.UtcNow);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
