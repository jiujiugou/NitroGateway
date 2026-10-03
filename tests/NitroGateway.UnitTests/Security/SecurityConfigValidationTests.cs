using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Identity;
using NitroGateway.Security;
using NitroGateway.Security.Auth;
using NitroGateway.Security.Guard;
using Xunit;

namespace NitroGateway.UnitTests.Security;

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

    [Fact]
    public void PlaintextDefaultPassword_UnderProductionEnv_Throws()
    {
        var config = BuildConfig(
            ("Security:JwtSecretKey", StrongKey),
            ("Security:ExpireHours", "8"),
            ("Security:Users:0:Username", "admin"),
            ("Security:Users:0:Password", "admin123"),
            ("Security:Users:0:Role", "Admin"),
            ("DOTNET_ENVIRONMENT", "Production"));

        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddNitroSecurity(config));
    }

    [Fact]
    public void PlaintextStrongPassword_UnderProductionEnv_IsHashedOnRegistration()
    {
        // compose/.env 以明文强密码覆盖 → 启动通过，且注册的 JwtConfig 已归一化为哈希，
        // TokenGenerator 可正常校验（修复前明文直接登录 500：Base-64 解析异常）。
        var config = BuildConfig(
            ("Security:JwtSecretKey", StrongKey),
            ("Security:ExpireHours", "8"),
            ("Security:Users:0:Username", "admin"),
            ("Security:Users:0:Password", "A-Strong-P@ssw0rd!"),
            ("Security:Users:0:Role", "Admin"),
            ("DOTNET_ENVIRONMENT", "Production"));

        var services = new ServiceCollection();
        services.AddNitroSecurity(config);
        var jwtConfig = services.BuildServiceProvider().GetRequiredService<JwtConfig>();

        var user = Assert.Single(jwtConfig.Users);
        Assert.Equal("admin", user.Username);
        var hasher = new PasswordHasher<UserConfig>();
        Assert.NotEqual(
            PasswordVerificationResult.Failed,
            hasher.VerifyHashedPassword(user, user.Password, "A-Strong-P@ssw0rd!"));
    }

    [Fact]
    public void PlaintextDefaultPassword_UnderDevelopmentEnv_DoesNotThrow()
    {
        // 开发环境保留明文默认密码，仅归一化哈希，不影响本地调试
        var config = BuildConfig(
            ("Security:JwtSecretKey", StrongKey),
            ("Security:ExpireHours", "8"),
            ("Security:Users:0:Username", "admin"),
            ("Security:Users:0:Password", "admin123"),
            ("Security:Users:0:Role", "Admin"),
            ("ASPNETCORE_ENVIRONMENT", "Development"));

        var ex = Record.Exception(() => new ServiceCollection().AddNitroSecurity(config));
        Assert.Null(ex);
    }

    /// <summary>有强密钥时核心服务全部可从容器解析（DI 接线不许漏注册）。</summary>
    [Fact]
    public void ValidConfig_RegistersCoreServices()
    {
        var config = BuildConfig(("Security:JwtSecretKey", StrongKey), ("Security:ExpireHours", "8"));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNitroSecurity(config);
        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<JwtConfig>());
        Assert.NotNull(provider.GetRequiredService<IReadOnlyList<UserConfig>>());
        Assert.NotNull(provider.GetRequiredService<PasswordHasher<UserAccount>>());
        Assert.NotNull(provider.GetRequiredService<RangeValidator>());
        Assert.NotNull(provider.GetRequiredService<RateLimitValidator>());
        Assert.NotNull(provider.GetRequiredService<ModeValidator>());
        Assert.NotNull(provider.GetRequiredService<WriteGuard>());
        Assert.NotNull(provider.GetRequiredService<LoginRateLimiter>());
    }

    /// <summary>开发占位密钥（NitroGateway-Dev 前缀）→ 替换为随机强密钥，不抛异常。</summary>
    [Fact]
    public void DevPlaceholderSecretKey_IsReplacedWithRandom()
    {
        const string devKey = "NitroGateway-Dev-0123456789abcdef0123456789abcdef";
        var config = BuildConfig(("Security:JwtSecretKey", devKey), ("Security:ExpireHours", "8"));
        var services = new ServiceCollection();
        services.AddNitroSecurity(config);
        using var provider = services.BuildServiceProvider();

        var jwt = provider.GetRequiredService<JwtConfig>();
        Assert.NotEqual(devKey, jwt.JwtSecretKey);
        Assert.True(jwt.JwtSecretKey.Length >= 32);
    }

    private static string HashPassword(string plain)
        => new PasswordHasher<UserConfig>().HashPassword(
            new UserConfig { Username = "admin", Password = "", Role = "Admin" }, plain);
}
