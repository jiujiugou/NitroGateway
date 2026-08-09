using NitroGateway.Security.Auth;
using Xunit;

namespace NitroGateway.UnitTests;

/// <summary>ADR-004 P2-1：登录失败计数 + 短时锁定</summary>
public class LoginRateLimiterTests
{
    [Fact]
    public void BelowThreshold_NotLocked()
    {
        var limiter = new LoginRateLimiter(maxFailures: 3);
        limiter.RecordFailure("admin|1.2.3.4");
        limiter.RecordFailure("admin|1.2.3.4");

        Assert.False(limiter.IsLocked("admin|1.2.3.4", out _));
    }

    [Fact]
    public void ReachingThreshold_Locks()
    {
        var limiter = new LoginRateLimiter(maxFailures: 3);
        limiter.RecordFailure("admin|1.2.3.4");
        limiter.RecordFailure("admin|1.2.3.4");
        limiter.RecordFailure("admin|1.2.3.4");

        Assert.True(limiter.IsLocked("admin|1.2.3.4", out var remaining));
        Assert.True(remaining > TimeSpan.Zero);
    }

    [Fact]
    public void DifferentKey_NotAffected()
    {
        var limiter = new LoginRateLimiter(maxFailures: 2);
        limiter.RecordFailure("admin|1.2.3.4");
        limiter.RecordFailure("admin|1.2.3.4");

        Assert.False(limiter.IsLocked("admin|5.6.7.8", out _));
    }

    [Fact]
    public void Reset_ClearsLock()
    {
        var limiter = new LoginRateLimiter(maxFailures: 2);
        limiter.RecordFailure("admin|1.2.3.4");
        limiter.RecordFailure("admin|1.2.3.4");
        Assert.True(limiter.IsLocked("admin|1.2.3.4", out _));

        limiter.Reset("admin|1.2.3.4");

        Assert.False(limiter.IsLocked("admin|1.2.3.4", out _));
    }

    [Fact]
    public void LockExpires_AfterDuration()
    {
        var limiter = new LoginRateLimiter(maxFailures: 2, lockDuration: TimeSpan.FromMilliseconds(50));
        limiter.RecordFailure("admin|1.2.3.4");
        limiter.RecordFailure("admin|1.2.3.4");
        Assert.True(limiter.IsLocked("admin|1.2.3.4", out _));

        Thread.Sleep(100);

        Assert.False(limiter.IsLocked("admin|1.2.3.4", out _));
    }

    [Fact]
    public void ExpiredEntries_AreTrimmed_WhenOverCapacity()
    {
        // ADR-022 P3-3：条目超上限时清理窗口已过期的记录，字典有界
        var limiter = new LoginRateLimiter(maxFailures: 3, window: TimeSpan.FromMilliseconds(50), maxEntries: 2);
        limiter.RecordFailure("expired|1");

        Thread.Sleep(80);

        limiter.RecordFailure("fresh-a|1");
        limiter.RecordFailure("fresh-b|1");   // 触发 trim，清掉已过期的 expired|1

        Assert.Equal(2, limiter.Count);
    }
}
