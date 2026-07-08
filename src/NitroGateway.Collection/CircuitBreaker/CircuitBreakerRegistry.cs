using System.Collections.Concurrent;

namespace NitroGateway.Collection;

/// <summary>熔断器注册表实现。线程安全，按设备 ID 管理独立熔断器</summary>
public sealed class CircuitBreakerRegistry : ICircuitBreakerRegistry
{
    private readonly ConcurrentDictionary<string, ICircuitBreaker> _map = new();

    private readonly int _failureThreshold;
    private readonly TimeSpan _openDuration;
    private readonly TimeSpan _maxOpenDuration;

    /// <summary>
    /// 创建熔断器注册表。
    /// </summary>
    /// <param name="failureThreshold">连续失败多少次后断开。默认 5</param>
    /// <param name="openDuration">断开后冷却多久进入半开探测。默认 30 秒</param>
    /// <param name="maxOpenDuration">最大冷却时间（指数退避上限）。默认 5 分钟</param>
    public CircuitBreakerRegistry(
        int failureThreshold = 5,
        TimeSpan? openDuration = null,
        TimeSpan? maxOpenDuration = null)
    {
        _failureThreshold = failureThreshold;
        _openDuration = openDuration ?? TimeSpan.FromSeconds(30);
        _maxOpenDuration = maxOpenDuration ?? TimeSpan.FromMinutes(5);
    }

    /// <inheritdoc />
    public ICircuitBreaker Get(Guid deviceId)
    {
        return _map.GetOrAdd(
            deviceId.ToString(),
            _ => new CircuitBreaker(_failureThreshold, _openDuration, _maxOpenDuration));
    }

    /// <inheritdoc />
    public void Reset(Guid deviceId)
    {
        if (_map.TryGetValue(deviceId.ToString(), out var breaker))
        {
            breaker.Reset();
        }
    }
}
