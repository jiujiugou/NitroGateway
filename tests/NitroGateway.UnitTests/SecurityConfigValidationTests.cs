using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Identity;
using NitroGateway.Security;
using NitroGateway.Security.Auth;
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

    [Fact]
    public void DefaultTestPassword_UnderProductionEnv_Throws()
    {
        // ADR-052 问题2：生产环境仍用默认测试密码 admin123 → 拒绝启动（防测试账号带上生产）
        var config = BuildConfig(
            ("Security:JwtSecretKey", StrongKey),
            ("Security:ExpireHours", "8"),
            ("Security:Users:0:Username", "admin"),
            ("Security:Users:0:Password", HashPassword("admin123")),
            ("Security:Users:0:Role", "Admin"),
            ("DOTNET_ENVIRONMENT", "Production"));

        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddNitroSecurity(config));
    }

    [Fact]
    public void DefaultTestPassword_UnderDevelopmentEnv_DoesNotThrow()
    {
        // 开发环境保留测试账号，不影响本地调试
        var config = BuildConfig(
            ("Security:JwtSecretKey", StrongKey),
            ("Security:ExpireHours", "8"),
            ("Security:Users:0:Username", "admin"),
            ("Security:Users:0:Password", HashPassword("admin123")),
            ("Security:Users:0:Role", "Admin"),
            ("ASPNETCORE_ENVIRONMENT", "Development"));

        var ex = Record.Exception(() => new ServiceCollection().AddNitroSecurity(config));
        Assert.Null(ex);
    }

    [Fact]
    public void StrongPassword_UnderProductionEnv_DoesNotThrow()
    {
        var config = BuildConfig(
            ("Security:JwtSecretKey", StrongKey),
            ("Security:ExpireHours", "8"),
            ("Security:Users:0:Username", "admin"),
            ("Security:Users:0:Password", HashPassword("A-Strong-P@ssw0rd!")),
            ("Security:Users:0:Role", "Admin"),
            ("DOTNET_ENVIRONMENT", "Production"));

        var ex = Record.Exception(() => new ServiceCollection().AddNitroSecurity(config));
        Assert.Null(ex);
    }

    private static string HashPassword(string plain)
        => new PasswordHasher<UserConfig>().HashPassword(
            new UserConfig { Username = "admin", Password = "", Role = "Admin" }, plain);
}
