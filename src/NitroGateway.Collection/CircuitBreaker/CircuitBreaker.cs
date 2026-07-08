namespace NitroGateway.Collection;

/// <summary>熔断器状态</summary>
public enum CircuitState
{
    /// <summary>正常通行，失败计数器累加中</summary>
    Closed,
    /// <summary>断路，拒绝所有请求</summary>
    Open,
    /// <summary>半开探测，允许一个请求通过以验证恢复</summary>
    HalfOpen
}

/// <summary>
/// 单设备熔断器，线程安全。
/// 三态：Closed →（连续失败 N 次）→ Open →（冷却到期）→ HalfOpen →（探测成功 → Closed / 探测失败 → Open）
/// 每次重新打开时冷却时间翻倍，直到上限。
/// </summary>
public sealed class CircuitBreaker : ICircuitBreaker
{
    private readonly object _lock = new();
    private readonly int _failureThreshold;
    private readonly TimeSpan _baseOpenDuration;
    private readonly TimeSpan _maxOpenDuration;

    private CircuitState _state = CircuitState.Closed;
    private int _failureCount;
    private int _consecutiveSuccesses;
    private DateTime _openUntil = DateTime.MinValue;
    private TimeSpan _currentOpenDuration;
    private bool _probing;

    /// <summary>
    /// 创建熔断器。
    /// </summary>
    /// <param name="failureThreshold">连续失败多少次后断开。默认 5</param>
    /// <param name="openDuration">断开后冷却多久进入半开探测。默认 30 秒</param>
    /// <param name="maxOpenDuration">最大冷却时间（指数退避上限）。默认 5 分钟</param>
    public CircuitBreaker(
        int failureThreshold = 5,
        TimeSpan? openDuration = null,
        TimeSpan? maxOpenDuration = null)
    {
        _failureThreshold = failureThreshold > 0 ? failureThreshold : throw new ArgumentOutOfRangeException(nameof(failureThreshold));
        _baseOpenDuration = openDuration ?? TimeSpan.FromSeconds(30);
        _maxOpenDuration = maxOpenDuration ?? TimeSpan.FromMinutes(5);
        _currentOpenDuration = _baseOpenDuration;
    }

    /// <summary>当前状态（诊断用）</summary>
    public CircuitState State
    {
        get { lock (_lock) return ComputeState(); }
    }

    /// <inheritdoc />
    public bool IsOpen
    {
        get
        {
            lock (_lock)
            {
                var state = ComputeState();

                if (state == CircuitState.Open)
                    return true;

                if (state == CircuitState.HalfOpen)
                {
                    if (_probing)
                        return true;   // 已有探测在进行，拒绝新的
                    _probing = true;    // 第一次进入 HalfOpen：放行
                    return false;
                }

                return false;   // Closed：放行
            }
        }
    }

    /// <inheritdoc />
    public void RecordSuccess()
    {
        lock (_lock)
        {
            _failureCount = 0;
            _consecutiveSuccesses++;

            if (_state == CircuitState.HalfOpen && _consecutiveSuccesses >= 1)
            {
                // 探测成功 → 恢复闭合，重置冷却时间
                _state = CircuitState.Closed;
                _currentOpenDuration = _baseOpenDuration;
                _probing = false;
            }
        }
    }

    /// <inheritdoc />
    public void RecordFailure()
    {
        lock (_lock)
        {
            _failureCount++;
            _consecutiveSuccesses = 0;

            if (_state == CircuitState.HalfOpen)
            {
                // 探测失败 → 重新打开，冷却时间翻倍
                _state = CircuitState.Open;
                _currentOpenDuration = TimeSpan.FromTicks(
                    Math.Min(_currentOpenDuration.Ticks * 2, _maxOpenDuration.Ticks));
                _openUntil = DateTime.UtcNow + _currentOpenDuration;
                _probing = false;
            }
            else if (_state == CircuitState.Closed && _failureCount >= _failureThreshold)
            {
                // 连续失败达阈值 → 打开
                _state = CircuitState.Open;
                _openUntil = DateTime.UtcNow + _currentOpenDuration;
            }
        }
    }

    /// <summary>
    /// 强制重置熔断器到闭合状态（用于设备恢复、手动干预）。
    /// </summary>
    public void Reset()
    {
        lock (_lock)
        {
            _state = CircuitState.Closed;
            _failureCount = 0;
            _consecutiveSuccesses = 0;
            _currentOpenDuration = _baseOpenDuration;
            _probing = false;
        }
    }

    // ── 内部 ──

    /// <summary>检查是否该从 Open 转到 HalfOpen</summary>
    private CircuitState ComputeState()
    {
        if (_state == CircuitState.Open && DateTime.UtcNow >= _openUntil)
        {
            _state = CircuitState.HalfOpen;
        }
        return _state;
    }
}
