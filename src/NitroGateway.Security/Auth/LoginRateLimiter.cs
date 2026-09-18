using System.Collections.Concurrent;

namespace NitroGateway.Security.Auth;

public sealed class LoginRateLimiter
{
    private sealed record Entry(int Failures, DateTimeOffset FirstFailureAt, DateTimeOffset? LockedUntil);

    private readonly ConcurrentDictionary<string, Entry> _entries = new();
    private readonly int _maxFailures;
    private readonly TimeSpan _window;
    private readonly TimeSpan _lockDuration;
    private readonly int _maxEntries;

    /// <param name="maxFailures">窗口内失败多少次触发锁定，默认 5</param>
    /// <param name="window">失败计数窗口，默认 10 分钟</param>
    /// <param name="lockDuration">达到阈值后的锁定时长，默认 60 秒</param>
    /// <param name="maxEntries">条目数上限，超限时清理过期条目，默认 10000</param>
    public LoginRateLimiter(int maxFailures = 5, TimeSpan? window = null, TimeSpan? lockDuration = null, int maxEntries = 10_000)
    {
        _maxFailures = Math.Max(1, maxFailures);
        _window = window ?? TimeSpan.FromMinutes(10);
        _lockDuration = lockDuration ?? TimeSpan.FromSeconds(60);
        _maxEntries = Math.Max(1, maxEntries);
    }

    /// <summary>当前条目数（诊断用，验证字典有界）</summary>
    public int Count => _entries.Count;

    /// <summary>是否处于锁定状态；锁定中返回剩余时间</summary>
    public bool IsLocked(string key, out TimeSpan remaining)
    {
        var now = DateTimeOffset.UtcNow;
        if (_entries.TryGetValue(key, out var e) && e.LockedUntil is { } until && until > now)
        {
            remaining = until - now;
            return true;
        }

        remaining = TimeSpan.Zero;
        return false;
    }

    /// <summary>记录一次登录失败；达到阈值后触发锁定</summary>
    public void RecordFailure(string key)
    {
        var now = DateTimeOffset.UtcNow;
        TrimExpired(now);
        _entries.AddOrUpdate(key,
            _ => new Entry(1, now, null),
            (_, e) =>
            {
                // 已锁定：保持锁定状态，不重复计时
                if (e.LockedUntil is { } until && until > now)
                    return e;

                // 窗口已过期：重新计数
                if (now - e.FirstFailureAt > _window)
                    return new Entry(1, now, null);

                var failures = e.Failures + 1;
                DateTimeOffset? lockedUntil = failures >= _maxFailures ? now + _lockDuration : null;
                return new Entry(failures, e.FirstFailureAt, lockedUntil);
            });
    }

    /// <summary>登录成功后清除计数</summary>
    public void Reset(string key) => _entries.TryRemove(key, out _);

    private void TrimExpired(DateTimeOffset now)
    {
        if (_entries.Count < _maxEntries) return;
        foreach (var kv in _entries)
        {
            // 先腾出空间再判断：Count 达到上限时本次 RecordFailure 还会新增一条
            if (_entries.Count < _maxEntries) break;
            if (now - kv.Value.FirstFailureAt > _window)
                _entries.TryRemove(kv.Key, out _);
        }
    }
}
