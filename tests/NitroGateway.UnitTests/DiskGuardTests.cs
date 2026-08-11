using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NitroGateway.Persistence.Sqlite;
using NitroGateway.Storage.Disk;
using Xunit;

namespace NitroGateway.UnitTests;

/// <summary>
/// ADR-012 磁盘守卫测试：阈值判断 / Critical 优先 / 滞后恢复 / 等级变化事件。
/// 核心逻辑（Evaluate）为静态纯函数，直接红绿对照；事件路径经 CheckOnceAsync 用超大阈值触发。
/// </summary>
public sealed class DiskGuardTests
{
    private const long Gb = 1024L * 1024 * 1024;
    private const long Mb = 1024L * 1024;

    private static DiskGuardOption Option(long warning = 1 * Gb, long critical = 256 * Mb, double margin = 20)
        => new()
        {
            WarningFreeBytes = warning,
            CriticalFreeBytes = critical,
            RecoveryMarginPercent = margin
        };

    [Fact]
    public void Plenty_of_space_is_healthy()
    {
        Assert.Equal(DiskLevel.Healthy, DiskGuardService.Evaluate(10 * Gb, Option(), DiskLevel.Healthy));
    }

    [Fact]
    public void Below_warning_threshold_is_warning()
    {
        Assert.Equal(DiskLevel.Warning, DiskGuardService.Evaluate(500 * Mb, Option(), DiskLevel.Healthy));
    }

    [Fact]
    public void Below_critical_threshold_is_critical()
    {
        Assert.Equal(DiskLevel.Critical, DiskGuardService.Evaluate(100 * Mb, Option(), DiskLevel.Healthy));
    }

    [Fact]
    public void Critical_takes_precedence_over_warning()
    {
        var option = Option(warning: 1 * Gb, critical: 256 * Mb);
        Assert.Equal(DiskLevel.Critical, DiskGuardService.Evaluate(100 * Mb, option, DiskLevel.Warning));
    }

    [Fact]
    public void Recovery_from_critical_requires_margin()
    {
        var option = Option(critical: 256 * Mb);
        // 刚过原始阈值（×1.1 < ×1.2）仍保持 Critical，防临界抖动
        Assert.Equal(DiskLevel.Critical, DiskGuardService.Evaluate((long)(256 * Mb * 1.1), option, DiskLevel.Critical));
        // 超过滞后阈值（×1.3 > ×1.2）先回 Warning（仍低于 Warning 阈值），再随空间恢复回到 Healthy
        Assert.Equal(DiskLevel.Warning, DiskGuardService.Evaluate((long)(256 * Mb * 1.3), option, DiskLevel.Critical));
        Assert.Equal(DiskLevel.Healthy, DiskGuardService.Evaluate(2 * Gb, option, DiskLevel.Critical));
    }

    [Fact]
    public void Recovery_from_warning_requires_margin()
    {
        var option = Option(warning: 1 * Gb);
        Assert.Equal(DiskLevel.Warning, DiskGuardService.Evaluate((long)(1 * Gb * 1.1), option, DiskLevel.Warning));
        Assert.Equal(DiskLevel.Healthy, DiskGuardService.Evaluate((long)(1 * Gb * 1.3), option, DiskLevel.Warning));
    }

    [Fact]
    public async Task Level_change_raises_event()
    {
        // 超大阈值保证真实磁盘必然触发 Critical；恢复场景由 Evaluate 单测覆盖，不依赖真实磁盘
        var service = new DiskGuardService(
            "Data Source=nitrogateway.db",
            Options.Create(new DiskGuardOption
            {
                WarningFreeBytes = long.MaxValue / 2 + 1,
                CriticalFreeBytes = long.MaxValue / 2,
                RecoveryMarginPercent = 20
            }),
            NullLogger<DiskGuardService>.Instance);

        DiskLevel? changedTo = null;
        service.Changed += level => changedTo = level;

        await service.CheckOnceAsync();

        Assert.Equal(DiskLevel.Critical, service.Level);
        Assert.Equal(DiskLevel.Critical, changedTo);
    }
}
