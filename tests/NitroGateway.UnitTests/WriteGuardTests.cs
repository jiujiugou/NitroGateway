using Microsoft.Extensions.Logging.Abstractions;
using NitroGateway.Security.Guard;
using Xunit;

namespace NitroGateway.UnitTests;

/// <summary>
/// 写指令门控 WriteGuard 测试。
///
/// <para>WriteGuard 是写操作的最后一道安全防线——在 IProtocolDriver.WriteAsync 之前，
/// 三级校验器依次检查：设备是否可写入？值是否在允许范围？变化率是否异常？</para>
///
/// <para>任何一级校验失败都会拒绝写入并记录日志。这防止了：
/// 1. 向已停机的设备发送控制命令
/// 2. 写入超范围值导致设备损坏
/// 3. 瞬时大跳变（PLC 编程错误或网络攻击）</para>
///
/// <para>测试覆盖 9 个场景：正常写入、设备状态拒绝、范围拒绝、变化率拒绝、首次写入、零值跳过。</para>
/// </summary>
public class WriteGuardTests
{
    private readonly WriteGuard _guard;

    public WriteGuardTests()
    {
        _guard = new WriteGuard(
            new RangeValidator(), new RateLimitValidator(), new ModeValidator(),
            NullLogger<WriteGuard>.Instance);
    }

    // ═══════════════════ 正常通过 ═══════════════════

    /// <summary>正常写入：设备 Online + 值在范围内 + 变化率正常 → 全部校验通过。</summary>
    [Fact]
    public void NormalWrite_PassesAllChecks()
    {
        var cmd = new WriteCommand
        {
            DeviceId = Guid.NewGuid(), PointId = Guid.NewGuid(),
            Value = 50, DeviceStatus = "Online", MaxLimit = 100, MinLimit = 0
        };
        var result = _guard.Evaluate(cmd);
        Assert.True(result.IsValid);
    }

    // ═══════════════════ ModeValidator ═══════════════════

    /// <summary>
    /// 设备 Offline 时拒绝写入。工业安全第一原则——已停机的设备不接受任何控制命令。
    /// Maintenance 状态的设备也不应接受写入（虽然能采集数据）。
    /// </summary>
    [Fact]
    public void DeviceOffline_Rejected()
    {
        var cmd = new WriteCommand
        {
            DeviceId = Guid.NewGuid(), PointId = Guid.NewGuid(),
            Value = 50, DeviceStatus = "Offline"
        };
        var result = _guard.Evaluate(cmd);
        Assert.False(result.IsValid);
    }

    // ═══════════════════ RangeValidator ═══════════════════

    /// <summary>值超出配置上限（150 > 100）→ 拒绝。</summary>
    [Fact]
    public void ValueExceedsMax_Rejected()
    {
        var cmd = new WriteCommand
        {
            DeviceId = Guid.NewGuid(), PointId = Guid.NewGuid(),
            Value = 150, DeviceStatus = "Online", MaxLimit = 100
        };
        var result = _guard.Evaluate(cmd);
        Assert.False(result.IsValid);
    }

    /// <summary>值低于配置下限（-10 < 0）→ 拒绝。</summary>
    [Fact]
    public void ValueBelowMin_Rejected()
    {
        var cmd = new WriteCommand
        {
            DeviceId = Guid.NewGuid(), PointId = Guid.NewGuid(),
            Value = -10, DeviceStatus = "Online", MinLimit = 0
        };
        var result = _guard.Evaluate(cmd);
        Assert.False(result.IsValid);
    }

    /// <summary>
    /// 无范围限制（MinLimit=null, MaxLimit=null）时跳过范围校验。
    /// 很多点位没有配置范围限制——此时不应错误拒绝写入。
    /// </summary>
    [Fact]
    public void NoRangeLimit_PassesRangeCheck()
    {
        var cmd = new WriteCommand
        {
            DeviceId = Guid.NewGuid(), PointId = Guid.NewGuid(),
            Value = 9999, DeviceStatus = "Online", MaxLimit = null, MinLimit = null
        };
        var result = _guard.Evaluate(cmd);
        Assert.True(result.IsValid);
    }

    // ═══════════════════ RateLimitValidator ═══════════════════

    /// <summary>
    /// 变化率超限 (120-50)/50 = 140% > 100% → 拒绝。
    /// 防止 PLC 程序错误或网络攻击导致的值突变。
    /// </summary>
    [Fact]
    public void RateExceeded_Rejected()
    {
        var cmd = new WriteCommand
        {
            DeviceId = Guid.NewGuid(), PointId = Guid.NewGuid(),
            Value = 120, DeviceStatus = "Online", PreviousValue = 50
        };
        var result = _guard.Evaluate(cmd);
        Assert.False(result.IsValid);
    }

    /// <summary>变化率在 100% 以内（(75-50)/50=50%）→ 通过。</summary>
    [Fact]
    public void RateNormal_Passes()
    {
        var cmd = new WriteCommand
        {
            DeviceId = Guid.NewGuid(), PointId = Guid.NewGuid(),
            Value = 75, DeviceStatus = "Online", PreviousValue = 50
        };
        var result = _guard.Evaluate(cmd);
        Assert.True(result.IsValid);
    }

    /// <summary>首次写入无 PreviousValue → 跳过变化率校验。</summary>
    [Fact]
    public void FirstWrite_SkipsRateCheck()
    {
        var cmd = new WriteCommand
        {
            DeviceId = Guid.NewGuid(), PointId = Guid.NewGuid(),
            Value = 9999, DeviceStatus = "Online", PreviousValue = null
        };
        var result = _guard.Evaluate(cmd);
        Assert.True(result.IsValid);
    }

    /// <summary>PreviousValue=0 时跳过变化率校验（避免除以零异常）。</summary>
    [Fact]
    public void PreviousValueZero_SkipsRateCheck()
    {
        var cmd = new WriteCommand
        {
            DeviceId = Guid.NewGuid(), PointId = Guid.NewGuid(),
            Value = 100, DeviceStatus = "Online", PreviousValue = 0
        };
        var result = _guard.Evaluate(cmd);
        Assert.True(result.IsValid);
    }
}
