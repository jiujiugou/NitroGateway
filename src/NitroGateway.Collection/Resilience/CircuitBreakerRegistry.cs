using System.Collections.Concurrent;

namespace NitroGateway.Collection;

/// <summary>熔断器注册表实现。线程安全，按设备 ID 管理独立熔断器</summary>
public sealed class CircuitBreakerRegistry : ICircuitBreakerRegistry
{
    private readonly ConcurrentDictionary<string, ICircuitBreaker> _map = new();

    private readonly TimeSpan _baseOpenDuration;
    private readonly TimeSpan _maxOpenDuration;

    /// <summary>
    /// 创建熔断器注册表。
    /// </summary>
    /// <param name="baseOpenDuration">起步冷却时间。默认 5 秒</param>
    /// <param name="maxOpenDuration">最大冷却时间（翻倍上限）。默认 5 分钟</param>
    public CircuitBreakerRegistry(
        TimeSpan? baseOpenDuration = null,
        TimeSpan? maxOpenDuration = null)
    {
        _baseOpenDuration = baseOpenDuration ?? TimeSpan.FromSeconds(5);
        _maxOpenDuration = maxOpenDuration ?? TimeSpan.FromMinutes(5);
    }

    /// <inheritdoc />
    public ICircuitBreaker Get(Guid deviceId)
    {
        return _map.GetOrAdd(
            deviceId.ToString(),
            _ => new CircuitBreaker(_baseOpenDuration, _maxOpenDuration));
    }

    /// <inheritdoc />
    public void Reset(Guid deviceId)
    {
        if (_map.TryGetValue(deviceId.ToString(), out var breaker))
            breaker.Reset();
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<Guid, ICircuitBreaker> GetAll()
    {
        return _map.ToDictionary(kv => Guid.Parse(kv.Key), kv => kv.Value);
    }
}
