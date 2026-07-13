using NitroGateway.Alarm.Domain;
using NitroGateway.Alarm.Evaluation;
using Xunit;

namespace NitroGateway.UnitTests;

/// <summary>
/// 告警评估引擎单元测试。
///
/// <para>AlarmEvaluator 是告警子系统的核心——接收采集值 + 规则列表 → 产出 AlarmEvaluation。
/// 测试覆盖 Duration 计时、状态迁移（Pending→Active→Resolved）、去重、多规则并行评估、规则禁用。</para>
///
/// <para>此测试不依赖数据库——规则和数据都在内存中构建。</para>
/// </summary>
public class AlarmEvaluatorTests
{
    private readonly Guid _deviceId = Guid.NewGuid();
    private readonly Guid _pointId = Guid.NewGuid();

    // ══════════════════════════════════════════════════
    //  Duration 计时 — Pending → Active
    // ══════════════════════════════════════════════════

    /// <summary>
    /// 值首次超限但未达到 DurationSeconds → 输出 Pending 状态，不生成 Alarm 对象。
    /// 工业场景：温度偶尔跳变到 81℃ 但立即回落到 79℃，不应产生告警。
    /// Duration 的作用就是消除这种瞬时抖动。
    /// </summary>
    [Fact]
    public void Exceeded_NotEnoughDuration_ReturnsPending()
    {
        var rules = new[] { MakeRule(">", 80, durationSeconds: 5) };
        var evaluator = new AlarmEvaluator();
        var results = evaluator.Evaluate(_deviceId, _pointId, 85, rules, DateTime.UtcNow);
        Assert.Single(results);
        Assert.Equal(AlarmState.Pending, results[0].NewState);
        Assert.Null(results[0].Alarm);  // 未生成告警
    }

    /// <summary>
    /// 值超限持续时间 ≥ DurationSeconds → 输出 Active 状态，生成 Alarm 对象。
    /// Duration 秒后，确认这不是瞬时抖动，正式触发告警。
    /// </summary>
    [Fact]
    public void Exceeded_DurationSatisfied_ReturnsActive()
    {
        var rules = new[] { MakeRule(">", 80, durationSeconds: 2) };
        var evaluator = new AlarmEvaluator();
        var start = DateTime.UtcNow;

        evaluator.Evaluate(_deviceId, _pointId, 85, rules, start);           // t=0: Pending
        var results = evaluator.Evaluate(_deviceId, _pointId, 85, rules, start.AddSeconds(3));  // t=3s ≥ 2s → Active
        Assert.Single(results);
        Assert.Equal(AlarmState.Active, results[0].NewState);
        Assert.NotNull(results[0].Alarm);
        Assert.Equal(_deviceId, results[0].Alarm!.DeviceId);
    }

    /// <summary>
    /// Duration 计时应在值回落到正常范围时重置。
    /// 例如：超限 2 秒 → 回落 → 再超限 → 重新开始 Duration 计时。
    /// 这确保只有"持续"超限才触发告警。
    /// </summary>
    [Fact]
    public void DurationReset_WhenValueDropsBelowThreshold()
    {
        var rules = new[] { MakeRule(">", 80, durationSeconds: 5) };
        var evaluator = new AlarmEvaluator();
        var start = DateTime.UtcNow;

        evaluator.Evaluate(_deviceId, _pointId, 85, rules, start.AddSeconds(1));  // 开始计时
        evaluator.Evaluate(_deviceId, _pointId, 75, rules, start.AddSeconds(2));  // 值回落 → 计时重置

        var results = evaluator.Evaluate(_deviceId, _pointId, 85, rules, start.AddSeconds(3));  // 重新超限
        Assert.Single(results);
        Assert.Equal(AlarmState.Pending, results[0].NewState);  // 重新 Pending
    }

    // ══════════════════════════════════════════════════
    //  去重 — Active 期间不重复生成
    // ══════════════════════════════════════════════════

    /// <summary>
    /// 去重逻辑：某条规则已经产生 Active 告警后，后续超限值不应再产生新告警。
    /// 只有 Resolved 之后再次超限，才生成新告警。
    /// 工业场景：温度一直 85℃ → 只产生一条告警，而不是每秒一条。
    /// </summary>
    [Fact]
    public void Dedup_ActiveAlarm_PreventsDuplicate()
    {
        var rules = new[] { MakeRule(">", 80, durationSeconds: 0) };
        var evaluator = new AlarmEvaluator();

        var r1 = evaluator.Evaluate(_deviceId, _pointId, 85, rules, DateTime.UtcNow);
        Assert.Single(r1);
        Assert.Equal(AlarmState.Active, r1[0].NewState);  // 告警已 Active

        var r2 = evaluator.Evaluate(_deviceId, _pointId, 90, rules, DateTime.UtcNow.AddSeconds(1));
        Assert.Empty(r2);  // 不重复生成
    }

    // ══════════════════════════════════════════════════
    //  恢复 — Active → Resolved
    // ══════════════════════════════════════════════════

    /// <summary>
    /// 告警恢复测试：Active 状态 + 值回落 → Resolved。
    /// 返回的 AlarmEvaluation 中包含 ExistingAlarmId 供 AlarmHostedService 更新数据库。
    /// </summary>
    [Fact]
    public void Recovery_ActiveToResolved()
    {
        var rules = new[] { MakeRule(">", 80, durationSeconds: 0) };
        var evaluator = new AlarmEvaluator();

        evaluator.Evaluate(_deviceId, _pointId, 85, rules, DateTime.UtcNow);  // Active
        var results = evaluator.Evaluate(_deviceId, _pointId, 75, rules, DateTime.UtcNow.AddSeconds(1));

        Assert.Single(results);
        Assert.Equal(AlarmState.Resolved, results[0].NewState);
        Assert.NotEqual(Guid.Empty, results[0].ExistingAlarmId);
    }

    // ══════════════════════════════════════════════════
    //  多规则 — 一个点多个告警等级
    // ══════════════════════════════════════════════════

    /// <summary>
    /// 同一个采集值匹配多条规则（如 >50=Info, >70=Warning, >90=Critical），
    /// 每条规则独立评估，应产生对应数量的结果。
    /// 工业场景：温度 95℃ → Info + Warning + Critical 三条告警同时触发。
    /// </summary>
    [Fact]
    public void MultipleRules_EachEvaluatedIndependently()
    {
        var rules = new[]
        {
            MakeRule(">", 50, durationSeconds: 0, AlarmSeverity.Info),
            MakeRule(">", 70, durationSeconds: 0, AlarmSeverity.Warning),
            MakeRule(">", 90, durationSeconds: 0, AlarmSeverity.Critical),
        };
        var evaluator = new AlarmEvaluator();

        var results = evaluator.Evaluate(_deviceId, _pointId, 95, rules, DateTime.UtcNow);
        Assert.Equal(3, results.Count);
        Assert.Contains(results, r => r.Severity == AlarmSeverity.Info);
        Assert.Contains(results, r => r.Severity == AlarmSeverity.Warning);
        Assert.Contains(results, r => r.Severity == AlarmSeverity.Critical);
    }

    // ══════════════════════════════════════════════════
    //  规则禁用
    // ══════════════════════════════════════════════════

    /// <summary>
    /// Enabled=false 的规则应被 Evaluator 跳过，不参与评估也不产生结果。
    /// 工业场景：维护期间临时关闭某条规则，而不是删除它。
    /// </summary>
    [Fact]
    public void DisabledRule_Ignored()
    {
        var rule = MakeRule(">", 80, durationSeconds: 0);
        var disabledRule = new AlarmRule
        {
            Id = Guid.NewGuid(),  DeviceId = _deviceId, PointId = _pointId,
            Operator = ">", Threshold = 80, DurationSeconds = 0,
            Severity = AlarmSeverity.Warning, Enabled = false
        };
        var rules = new[] { rule, disabledRule };
        var evaluator = new AlarmEvaluator();

        var results = evaluator.Evaluate(_deviceId, _pointId, 85, rules, DateTime.UtcNow);
        Assert.Single(results);  // 只有启用的规则产生结果
    }

    /// <summary>创建告警规则</summary>
    private AlarmRule MakeRule(string op, double threshold, int durationSeconds = 0,
        AlarmSeverity severity = AlarmSeverity.Warning) => new()
    {
        Id = Guid.NewGuid(),
        DeviceId = _deviceId,
        PointId = _pointId,
        Operator = op,
        Threshold = threshold,
        DurationSeconds = durationSeconds,
        Severity = severity,
        Enabled = true
    };
}
