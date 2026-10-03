using NitroGateway.Persistence.Sqlite;
using Xunit;

namespace NitroGateway.UnitTests.Persistence;

/// <summary>DiskGuardOption 配置默认值与节名。</summary>
public class DiskGuardOptionTests
{
    [Fact]
    public void SectionName_IsDisk()
        => Assert.Equal("Disk", DiskGuardOption.SectionName);

    [Fact]
    public void Defaults_AreExpected()
    {
        var option = new DiskGuardOption();

        Assert.Equal(60, option.IntervalSeconds);
        Assert.Equal(1_073_741_824L, option.WarningFreeBytes);   // 1 GiB
        Assert.Equal(268_435_456L, option.CriticalFreeBytes);    // 256 MiB
        Assert.Equal(20, option.RecoveryMarginPercent);
    }
}
