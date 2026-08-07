using System.Collections.Concurrent;

namespace NitroGateway.Security.Auth;

/// <summary>
/// 登录失败限流（ADR-004 P2-1）。内存实现：按「用户名|IP」计数失败次数，
/// 达到阈值后短时锁定；仅作边缘网关内网防暴力破解的最小平卫，不做分布式/持久化。
/// 记录会在窗口过期后自然失效，字典规模受登录尝试量限制，无需主动清理。
/// </summary>
public sealed class LoginRateLimiter
{
    private sealed record Entry(int Failures, DateTimeOffset FirstFailureAt, DateTimeOffset? LockedUntil);

    private readonly ConcurrentDictionary<string, Entry> _entries = new();
    private readonly int _maxFailures;
    private readonly TimeSpan _window;
    private readonly TimeSpan _lockDuration;

    /// <param name="maxFailures">窗口内失败多少次触发锁定，默认 5</param>
    /// <param name="window">失败计数窗口，默认 10 分钟</param>
    /// <param name="lockDuration">达到阈值后的锁定时长，默认 60 秒</param>
    public LoginRateLimiter(int maxFailures = 5, TimeSpan? window = null, TimeSpan? lockDuration = null)
    {
        _maxFailures = Math.Max(1, maxFailures);
        _window = window ?? TimeSpan.FromMinutes(10);
        _lockDuration = lockDuration ?? TimeSpan.FromSeconds(60);
    }

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
}
