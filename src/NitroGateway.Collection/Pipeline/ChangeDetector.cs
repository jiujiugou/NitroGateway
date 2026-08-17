using System.Collections.Concurrent;
using NitroGateway.Domain.Devices;

namespace NitroGateway.Collection;

/// <summary>
/// 死区变化抑制器（ADR-053 第一刀）。维护每点「最后已存值/质量/时间」，从一批快照中筛出
/// 「应当放行」的子集（写库 + MQTT 转发 + SignalR 推送共用，三处语义一致）。
/// <para><b>语义（关键）：</b>
/// ① <see cref="PointSnapshot.Deadband"/> = 0（默认）→ 每样本都放行（向后兼容）；
/// ② &gt; 0 → |新值 − 最后已存值| <strong>&lt;</strong> Deadband 才抑制，≥ 则放行
/// （恰好等于阈值视为变化，与管线现有 <c>Math.Abs(...) &lt; point.Deadband</c> 抑制语义一致）；
/// ③ 首样本、质量变化（Good↔Bad/Uncertain）、心跳超时、Bool/String 值变化均强制放行。</para>
/// <para><b>与告警解耦：</b>本类缓存的是「最后已存值」，与管道的 `_lastValues`
/// （最后采集值，供告警 Duration）是两套独立状态，互不影响——告警仍按每样本判定。</para>
/// <para><b>线程安全：</b>状态存于 ConcurrentDictionary；单点只由采集热路径写入，
/// 无需跨轮加锁（同一轮内同点快照只出现一次）。</para>
/// </summary>
public sealed class ChangeDetector
{
    /// <summary>每点最近一次「已放行/已存」的状态（进程内缓存，重启丢失 → 首样本必写）。</summary>
    private sealed class PointState
    {
        /// <summary>最近已存工程值（数值点位）；Bool/String 点位不使用（值为 0 占位）。</summary>
        public double LastValue;

        /// <summary>最近已存原始值（Bool/String 点位做相等判定）。</summary>
        public object? LastRawValue;

        /// <summary>最近已存质量。</summary>
        public QualityCode LastQuality = QualityCode.Good;

        /// <summary>最近一次放行时刻（UTC），用于心跳兜底。</summary>
        public DateTime LastStoredAt;
    }

    /// <summary>心跳兜底间隔：超过该间隔即使值未变也强制放行，保留"还活着"的证据（ADR-053）。</summary>
    private readonly TimeSpan _heartbeat;

    /// <summary>点位 → 状态（按 DevicePointId 索引；不同设备同 ID 不冲突，点 ID 全局唯一）。</summary>
    private readonly ConcurrentDictionary<Guid, PointState> _states = new();

    /// <summary>
    /// 创建变化抑制器。
    /// </summary>
    /// <param name="heartbeat">心跳兜底间隔；必须大于 0（默认 5 分钟由配置 <c>Collection:DeadbandHeartbeatMs</c> 提供）。</param>
    public ChangeDetector(TimeSpan heartbeat)
    {
        if (heartbeat <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(heartbeat), "心跳兜底间隔必须大于 0");
        _heartbeat = heartbeat;
    }

    /// <summary>
    /// 从输入快照中筛出「应当放行」的子集（顺序与输入一致；可能为空 = 全部抑制）。
    /// 放行条件：首样本 / 质量变化 / 超死区变化 / 心跳超时 / Bool·String 值变化 / Deadband=0 全放行。
    /// </summary>
    /// <param name="snapshots">本轮采集的全量快照</param>
    /// <param name="nowUtc">当前 UTC 时间（供心跳判定与状态更新）</param>
    /// <returns>放行列表</returns>
    public IReadOnlyList<PointSnapshot> Filter(
        IReadOnlyList<PointSnapshot> snapshots, DateTime nowUtc)
    {
        var passed = new List<PointSnapshot>(snapshots.Count);
        foreach (var s in snapshots)
        {
            if (ShouldStore(s, nowUtc))
                passed.Add(s);
        }
        return passed;
    }

    // ---- 内部 ----

    /// <summary>判定单点快照是否放行；放行时同步更新状态缓存。规则见类注释。</summary>
    private bool ShouldStore(PointSnapshot s, DateTime nowUtc)
    {
        // 1. 首样本必写（无历史状态）：新点位 / 进程重启后的首条即新基线，无断档
        if (!_states.TryGetValue(s.DevicePointId, out var state))
        {
            UpdateState(s, nowUtc);
            return true;
        }

        // 2. 质量变化必写：Good↔Bad/Uncertain 切换必须落库，前端/告警才能看到掉线/恢复
        if (state.LastQuality != s.Quality)
        {
            UpdateState(s, nowUtc);
            return true;
        }

        // 3. 心跳兜底：超过心跳间隔即使值未变也强制放行（liveness + 时间对齐）
        if (nowUtc - state.LastStoredAt >= _heartbeat)
        {
            UpdateState(s, nowUtc);
            return true;
        }

        // 4. Bool/String：按值相等判定（无死区概念）；值未变且未到心跳 → 抑制
        if (s.DataType is DataType.Bool or DataType.String)
        {
            if (!Equals(state.LastRawValue, s.Value))
            {
                UpdateState(s, nowUtc);
                return true;
            }
            return false;
        }

        // 5. 数值：死区判定。Deadband=0 → 每样本都写（向后兼容，现有点位行为不变）
        if (s.Deadband <= 0)
        {
            UpdateState(s, nowUtc);
            return true;
        }

        // 数值无法转 double（如缩放失败 Value=null/非数值）→ 无法证明未变，保守放行（宁写勿丢）
        if (!TryGetDouble(s.Value, out var newVal))
        {
            UpdateState(s, nowUtc);
            return true;
        }

        // |Δ| < Deadband → 抑制；≥ Deadband（含恰好等于）→ 放行
        var delta = Math.Abs(newVal - state.LastValue);
        if (delta >= s.Deadband)
        {
            UpdateState(s, nowUtc);
            return true;
        }

        return false;
    }

    /// <summary>把本次放行的快照写入状态缓存（作为下一轮判定的基准）。</summary>
    private void UpdateState(PointSnapshot s, DateTime nowUtc)
    {
        var state = _states.GetOrAdd(s.DevicePointId, static _ => new PointState());
        state.LastValue = TryGetDouble(s.Value, out var d) ? d : 0;
        state.LastRawValue = s.Value;
        state.LastQuality = s.Quality;
        state.LastStoredAt = nowUtc;
    }

    /// <summary>尝试把值转换为 double（数值点位判定用）；null / 不可转换返回 false。</summary>
    private static bool TryGetDouble(object? value, out double result)
    {
        result = 0;
        try
        {
            if (value is null)
                return false;
            result = Convert.ToDouble(value);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
