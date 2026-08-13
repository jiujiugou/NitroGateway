using Microsoft.Extensions.Configuration;
using NitroGateway.Desktop.Services;
using NitroGateway.Shared;
using Xunit;

namespace NitroGateway.UnitTests;

/// <summary>
/// ADR-036 站点标识：SiteOptions 校验/生成、SiteSettingsStore 文件读写、SiteIdProvider 解析顺序。
/// </summary>
public sealed class SiteIdProviderTests
{
    private sealed class MemorySiteStore : ISiteSettingsStore
    {
        public SiteSettings Settings { get; set; } = new();
        public SiteSettings Load() => Settings;
        public void Save(SiteSettings settings) => Settings = settings;
    }

    private static IConfiguration Config(string? siteId)
    {
        var builder = new ConfigurationBuilder();
        if (siteId is not null)
            builder.AddInMemoryCollection(new Dictionary<string, string?> { ["Site:Id"] = siteId });
        return builder.Build();
    }

    [Theory]
    [InlineData("site-abc123", true)]
    [InlineData("plant-a", true)]
    [InlineData("a", true)]
    [InlineData("default", false)]   // 保留哨兵（未初始化）
    [InlineData("", false)]
    [InlineData("Site-ABC", false)]  // 大写不允许（topic 段规范）
    [InlineData("site/a", false)]    // / 是 topic 分隔符
    [InlineData("site+a", false)]    // + 是 topic 通配符
    [InlineData("site#a", false)]    // # 是 topic 通配符
    [InlineData("site a", false)]    // 空格
    [InlineData("-abc", false)]      // 必须以字母/数字开头
    [InlineData("012345678901234567890123456789012", false)] // 33 位超长
    public void IsValidSiteId_cases(string? siteId, bool expected)
        => Assert.Equal(expected, SiteOptions.IsValidSiteId(siteId));

    [Fact]
    public void GenerateSiteId_matches_format_and_differs_between_calls()
    {
        var a = SiteOptions.GenerateSiteId();
        var b = SiteOptions.GenerateSiteId();

        Assert.True(SiteOptions.IsValidSiteId(a));
        Assert.True(a.StartsWith("site-", StringComparison.Ordinal));
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Resolve_uses_configured_value_when_valid()
    {
        var store = new MemorySiteStore { Settings = new SiteSettings { SiteId = "stored-site" } };

        var resolved = SiteIdProvider.Resolve(Config("env-site"), store);

        Assert.Equal("env-site", resolved);
        Assert.Equal("stored-site", store.Settings.SiteId); // 不覆盖已存值
    }

    [Fact]
    public void Resolve_falls_back_to_store_when_config_unset_or_default()
    {
        var store = new MemorySiteStore { Settings = new SiteSettings { SiteId = "stored-site" } };

        Assert.Equal("stored-site", SiteIdProvider.Resolve(Config(null), store));
        Assert.Equal("stored-site", SiteIdProvider.Resolve(Config("default"), store)); // 哨兵视为未初始化
    }

    [Fact]
    public void Resolve_generates_and_persists_when_nothing_usable()
    {
        var store = new MemorySiteStore();

        var resolved = SiteIdProvider.Resolve(Config(null), store);

        Assert.True(SiteOptions.IsValidSiteId(resolved));
        Assert.Equal(resolved, store.Settings.SiteId);
    }

    [Fact]
    public void Save_rejects_invalid_and_persists_valid()
    {
        var store = new MemorySiteStore();
        var provider = new SiteIdProvider(Config(null), store);

        var bad = provider.Save("Bad/Site");
        Assert.True(bad.IsFailure);

        var good = provider.Save("plant-b");
        Assert.True(good.IsSuccess);
        Assert.Equal("plant-b", provider.Current);
        Assert.Equal("plant-b", store.Settings.SiteId);
    }

    [Fact]
    public void Regenerate_persists_new_value()
    {
        var store = new MemorySiteStore();
        var provider = new SiteIdProvider(Config(null), store);

        var value = provider.Regenerate();

        Assert.True(SiteOptions.IsValidSiteId(value));
        Assert.Equal(value, store.Settings.SiteId);
        Assert.Equal(value, provider.Current);
    }

    [Fact]
    public void Store_roundtrips_via_file()
    {
        var path = Path.Combine(Path.GetTempPath(), "ntg-tests", $"{Guid.NewGuid():N}.json");
        try
        {
            var store = new SiteSettingsStore(path);
            store.Save(new SiteSettings { SiteId = "site-xyz" });
            Assert.Equal("site-xyz", new SiteSettingsStore(path).Load().SiteId);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Store_corrupt_file_returns_empty()
    {
        var path = Path.Combine(Path.GetTempPath(), "ntg-tests", $"{Guid.NewGuid():N}.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{ not json");
            Assert.Equal("", new SiteSettingsStore(path).Load().SiteId);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}