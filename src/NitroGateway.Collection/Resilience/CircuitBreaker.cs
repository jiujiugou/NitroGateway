namespace NitroGateway.Collection;

/// <summary>
/// 单设备熔断器，线程安全。
/// <para><b>设计原则：HealthMonitor 是唯一的健康状态决策者。</b></para>
/// <para>
/// CircuitBreaker 只负责"保护执行"，不自己判定设备是否故障。
/// <see cref="Trip"/> 由 HealthMonitor 的 Offline 信号触发，
/// <see cref="Reset"/> 由 HealthMonitor 的 Online 信号触发。
/// </para>
/// <para><b>CQS 约定：</b><see cref="State"/> 是纯查询（无副作用）；
/// <see cref="TryEnterProbe"/> 是唯一带副作用的命令（推进 Open→HalfOpen、占用/释放探测名额），
/// 只允许采集执行路径调用，诊断/只读路径不得调用。</para>
/// <para><b>探测退避策略：</b></para>
/// <list type="bullet">
/// <item>Trip → Open（冷却 5s 起步）</item>
/// <item>冷却到期 → HalfOpen → 放行 1 个探测</item>
/// <item>探测成功 → Closed（恢复正常）</item>
/// <item>探测失败 → Open，冷却翻倍（5s→10s→20s→40s→...→5min 封顶）</item>
/// <item>下次 Trip 或 Reset → 冷却重置回 5s</item>
/// </list>
/// </summary>
public sealed class CircuitBreaker : ICircuitBreaker
{
    /// <summary>状态互斥锁；所有状态读写都在锁内进行，保证线程安全。</summary>
    private readonly object _lock = new();
    /// <summary>起步冷却时长，默认 5 秒。</summary>
    private readonly TimeSpan _baseOpenDuration;
    /// <summary>冷却翻倍上限，默认 5 分钟。</summary>
    private readonly TimeSpan _maxOpenDuration;

    /// <summary>当前熔断状态（Closed/Open/HalfOpen）。</summary>
    private CircuitState _state = CircuitState.Closed;
    /// <summary>Open 状态下冷却到期的时刻；到期后进入 HalfOpen。</summary>
    private DateTime _openUntil = DateTime.MinValue;
    /// <summary>当前探测开始时刻；用于探测超时（30 秒）自动释放。</summary>
    private DateTime _probeStarted = DateTime.MinValue;
    /// <summary>当前生效的冷却时长；探测失败时按指数翻倍，封顶 <see cref="_maxOpenDuration"/>。</summary>
    private TimeSpan _currentOpenDuration;
    /// <summary>是否已有探测请求在途；为 true 时 HalfOpen 拒绝其他请求。</summary>
    private bool _probing;

    /// <summary>探测请求超时（30s），超时后自动释放 _probing 锁</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 创建熔断器。
    /// </summary>
    /// <param name="baseOpenDuration">起步冷却时间。默认 5 秒</param>
    /// <param name="maxOpenDuration">最大冷却时间（翻倍上限）。默认 5 分钟</param>
    public CircuitBreaker(
        TimeSpan? baseOpenDuration = null,
        TimeSpan? maxOpenDuration = null)
    {
        _baseOpenDuration = baseOpenDuration ?? TimeSpan.FromSeconds(5);
        _maxOpenDuration = maxOpenDuration ?? TimeSpan.FromMinutes(5);
        _currentOpenDuration = _baseOpenDuration;
    }

    /// <summary>当前状态（诊断用，纯查询、无副作用；Open→HalfOpen 的推进由 <see cref="TryEnterProbe"/> 完成）</summary>
    public CircuitState State
    {
        get { lock (_lock) return _state; }
    }

    /// <inheritdoc />
    public bool TryEnterProbe()
    {
        lock (_lock)
        {
            var state = ComputeState();

            // Open: 冷却未到，拒绝
            if (state == CircuitState.Open)
                return false;

            // HalfOpen: 仅放行第一个请求作为探测
            if (state == CircuitState.HalfOpen)
            {
                if (_probing && DateTime.UtcNow - _probeStarted > ProbeTimeout)
                    // ADR-016 P3-5：探测卡住超 30s 自动释放，允许新探测进入——
                    // 若旧探测仍在途（如 TCP 超时 >30s），短暂出现两个并发探测，属有意放宽，
                    // 防止慢读永久阻塞恢复探测。
                    _probing = false;

                if (_probing)
                    return false;                // 已有探测在进行

                _probing = true;                 // 抢占唯一探测名额
                _probeStarted = DateTime.UtcNow;
                return true;
            }

            return true;                         // Closed: 正常放行
        }
    }

    /// <inheritdoc />
    /// <remarks>由 HealthMonitor Offline 信号触发，强制进入 Open，冷却复位到 5s。</remarks>
    public void Trip()
    {
        lock (_lock)
        {
            _state = CircuitState.Open;
            _currentOpenDuration = _baseOpenDuration;
            _openUntil = DateTime.UtcNow + _currentOpenDuration;
            _probing = false;
        }
    }

    /// <inheritdoc />
    /// <remarks>探测成功 → Closed，冷却重置回 5s。</remarks>
    public void RecordSuccess()
    {
        lock (_lock)
        {
            if (_state == CircuitState.HalfOpen)
            {
                _state = CircuitState.Closed;
                _currentOpenDuration = _baseOpenDuration;
                _probing = false;
                _probeStarted = DateTime.MinValue;
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>探测失败 → Open，冷却翻倍（上限 5min）。Closed 状态下忽略。</remarks>
    public void RecordFailure()
    {
        lock (_lock)
        {
            if (_state != CircuitState.HalfOpen)
                return;

            // 每次探测失败冷却翻倍
            _currentOpenDuration = TimeSpan.FromTicks(
                Math.Min(_currentOpenDuration.Ticks * 2, _maxOpenDuration.Ticks));

            _state = CircuitState.Open;
            _openUntil = DateTime.UtcNow + _currentOpenDuration;
            _probing = false;
            _probeStarted = DateTime.MinValue;
        }
    }

    /// <inheritdoc />
    /// <remarks>由 HealthMonitor Online 信号触发，强制恢复到 Closed，冷却复位。</remarks>
    public void Reset()
    {
        lock (_lock)
        {
            _state = CircuitState.Closed;
            _currentOpenDuration = _baseOpenDuration;
            _probing = false;
            _probeStarted = DateTime.MinValue;
        }
    }

    // ── 内部 ──

    /// <summary>检查是否该从 Open 转到 HalfOpen</summary>
    private CircuitState ComputeState()
    {
        if (_state == CircuitState.Open && DateTime.UtcNow >= _openUntil)
            _state = CircuitState.HalfOpen;
        return _state;
    }
}
