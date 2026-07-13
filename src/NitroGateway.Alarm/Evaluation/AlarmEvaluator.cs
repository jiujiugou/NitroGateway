using System.Collections.Concurrent;

namespace NitroGateway.Alarm.Evaluation;

/// <summary>
/// 告警评估引擎核心。
/// 不保存数据，只负责：接收 PointSnapshot → 查规则 → 比较 → Duration 判定 → 产生 Alarm 状态变更。
/// 内部维护 Duration 计时状态和去重映射。
/// </summary>
public sealed class AlarmEvaluator
{
    private readonly ConcurrentDictionary<Guid, RuleState> _states = new();

    /// <summary>
    /// 清除指定规则的运行时状态。应在规则被删除时调用，
    /// 避免 _states 字典只增不减导致的内存泄漏。
    /// </summary>
    public void ClearState(Guid ruleId)
    {
        _states.TryRemove(ruleId, out _);
    }

    /// <summary>
    /// 评估一个点位的采集值，返回触发的告警（新触发或恢复）。
    /// 调用方应处理返回结果：持久化新告警、更新状态、发送通知。
    /// </summary>
    /// <param name="pointId">点位 ID</param>
    /// <param name="deviceId">设备 ID</param>
    /// <param name="value">当前采集值</param>
    /// <param name="rules">该点位匹配的所有规则</param>
    /// <param name="now">当前时间（UTC）</param>
    /// <returns>产生的 Alarm 变更事件列表</returns>
    public List<AlarmEvaluation> Evaluate(
        Guid deviceId,
        Guid pointId,
        double value,
        IReadOnlyList<Domain.AlarmRule> rules,
        DateTime now)
    {
        var results = new List<AlarmEvaluation>();

        foreach (var rule in rules)
        {
            if (!rule.Enabled) continue;

            var state = _states.GetOrAdd(rule.Id, _ => new RuleState());
            var exceeded = ThresholdEvaluator.Evaluate(value, rule);

            if (exceeded)
            {
                HandleExceeded(rule, state, value, deviceId, pointId, now, results);
            }
            else
            {
                HandleNormal(rule, state, value, now, results);
            }

            state.LastValue = value;
        }

        return results;
    }

    // ════════════════════════════════════════════════
    //  超限处理
    // ════════════════════════════════════════════════

    private void HandleExceeded(
        Domain.AlarmRule rule,
        RuleState state,
        double value,
        Guid deviceId,
        Guid pointId,
        DateTime now,
        List<AlarmEvaluation> results)
    {
        // 去重：已有活跃/待确认告警，不重复生成
        if (state.ActiveAlarmId.HasValue)
            return;

        // 开始计时
        state.ExceedStartedAt ??= now;

        var elapsed = now - state.ExceedStartedAt.Value;
        if (elapsed.TotalSeconds < rule.DurationSeconds)
        {
            // 尚未满足 Duration，上报 Pending
            if (results.All(r => r.RuleId != rule.Id || r.NewState != Domain.AlarmState.Pending))
            {
                results.Add(new AlarmEvaluation
                {
                    RuleId = rule.Id,
                    DeviceId = deviceId,
                    PointId = pointId,
                    NewState = Domain.AlarmState.Pending,
                    TriggerValue = value,
                    Severity = rule.Severity,
                    ObservedAt = now
                });
            }
            return;
        }

        // 满足 Duration → Active
        var alarm = new Domain.Alarm
        {
            Id = Guid.NewGuid(),
            RuleId = rule.Id,
            DeviceId = deviceId,
            PointId = pointId,
            TriggerValue = value,
            Threshold = rule.Threshold,
            Severity = rule.Severity,
            Message = FormatMessage(rule, value),
            State = Domain.AlarmState.Active,
            FirstExceededAt = state.ExceedStartedAt.Value,
            OccurredAt = now
        };

        state.ActiveAlarmId = alarm.Id;

        results.Add(new AlarmEvaluation
        {
            RuleId = rule.Id,
            DeviceId = deviceId,
            PointId = pointId,
            NewState = Domain.AlarmState.Active,
            Alarm = alarm,
            TriggerValue = value,
            Severity = rule.Severity,
            ObservedAt = now
        });
    }

    // ════════════════════════════════════════════════
    //  恢复正常处理（告警抑制：设备 Down 时不触发恢复事件）
    // ════════════════════════════════════════════════

    private void HandleNormal(
        Domain.AlarmRule rule,
        RuleState state,
        double value,
        DateTime now,
        List<AlarmEvaluation> results)
    {
        // 从未超限
        if (state.ExceedStartedAt is null && state.ActiveAlarmId is null)
            return;

        // 重置 Duration 计时
        state.ExceedStartedAt = null;

        // 有活跃告警 → 恢复
        if (state.ActiveAlarmId.HasValue)
        {
            results.Add(new AlarmEvaluation
            {
                RuleId = rule.Id,
                DeviceId = Guid.Empty,
                PointId = Guid.Empty,
                NewState = Domain.AlarmState.Resolved,
                ExistingAlarmId = state.ActiveAlarmId.Value,
                TriggerValue = value,
                Severity = rule.Severity,
                ObservedAt = now
            });

            state.ActiveAlarmId = null;
        }
    }

    private static string FormatMessage(Domain.AlarmRule rule, double value)
    {
        if (!string.IsNullOrWhiteSpace(rule.MessageTemplate))
        {
            return rule.MessageTemplate
                .Replace("{value}", value.ToString("F2"))
                .Replace("{threshold}", rule.Threshold.ToString("F2"));
        }

        return $"值 {value:F2} {rule.Operator} {rule.Threshold}";
    }
}

/// <summary>评估结果</summary>
public sealed class AlarmEvaluation
{
    /// <summary>关联规则 ID</summary>
    public Guid RuleId { get; init; }

    /// <summary>设备 ID</summary>
    public Guid DeviceId { get; init; }

    /// <summary>点位 ID</summary>
    public Guid PointId { get; init; }

    /// <summary>目标状态</summary>
    public Domain.AlarmState NewState { get; init; }

    /// <summary>新告警对象（Active 时填充）</summary>
    public Domain.Alarm? Alarm { get; init; }

    /// <summary>已有告警 ID（Resolved 时填充）</summary>
    public Guid ExistingAlarmId { get; init; }

    /// <summary>触发值</summary>
    public double TriggerValue { get; init; }

    /// <summary>严重等级</summary>
    public Domain.AlarmSeverity Severity { get; init; }

    /// <summary>发生时间</summary>
    public DateTime ObservedAt { get; init; }
}
