using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NitroGateway.Security;
using Xunit;

namespace NitroGateway.UnitTests;

/// <summary>
/// ADR-004 P2-2/P2-3：JWT 配置与角色 fail-fast 校验。
/// </summary>
public class SecurityConfigValidationTests
{
    private static IConfiguration BuildConfig(params (string Key, string Value)[] items)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(items.ToDictionary(i => i.Key, i => (string?)i.Value))
            .Build();

    private const string StrongKey = "0123456789abcdef0123456789abcdef";

    [Fact]
    public void WeakSecretKey_Throws()
    {
        var config = BuildConfig(("Security:JwtSecretKey", "short"));

        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddNitroSecurity(config));
    }

    [Fact]
    public void ZeroExpireHours_Throws()
    {
        var config = BuildConfig(
            ("Security:JwtSecretKey", StrongKey),
            ("Security:ExpireHours", "0"));

        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddNitroSecurity(config));
    }

    [Fact]
    public void InvalidRole_Throws()
    {
        var config = BuildConfig(
            ("Security:JwtSecretKey", StrongKey),
            ("Security:ExpireHours", "8"),
            ("Security:Users:0:Username", "admin"),
            ("Security:Users:0:Password", "x"),
            ("Security:Users:0:Role", "SuperAdmin"));

        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddNitroSecurity(config));
    }

    [Fact]
    public void ValidConfig_DoesNotThrow()
    {
        var config = BuildConfig(
            ("Security:JwtSecretKey", StrongKey),
            ("Security:ExpireHours", "8"),
            ("Security:Users:0:Username", "admin"),
            ("Security:Users:0:Password", "hash"),
            ("Security:Users:0:Role", "Admin"));

        var ex = Record.Exception(() => new ServiceCollection().AddNitroSecurity(config));
        Assert.Null(ex);
    }
}
