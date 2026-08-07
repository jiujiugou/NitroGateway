using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using NitroGateway.Security.Auth;
using Xunit;

namespace NitroGateway.UnitTests;

/// <summary>
/// ADR-004 P1-2：Token 签发仅支持哈希密码（PasswordHasher 生成），
/// 明文 Equals 回退已移除。
/// </summary>
public class TokenGeneratorTests
{
    private static TokenGenerator CreateGenerator(params UserConfig[] users)
        => new(
            new JwtConfig { JwtSecretKey = new string('K', 64), Users = users.ToList() },
            users,
            NullLogger<TokenGenerator>.Instance);

    [Fact]
    public void IssueToken_HashedPassword_Succeeds()
    {
        var hasher = new PasswordHasher<UserConfig>();
        var user = new UserConfig
        {
            Username = "admin",
            Password = hasher.HashPassword(null!, "admin123"),
            Role = "Admin"
        };

        var token = CreateGenerator(user).IssueToken("admin", "admin123");

        Assert.NotNull(token);
    }

    [Fact]
    public void IssueToken_PlaintextConfiguredPassword_Fails()
    {
        var user = new UserConfig { Username = "admin", Password = "admin123", Role = "Admin" };

        var token = CreateGenerator(user).IssueToken("admin", "admin123");

        Assert.Null(token);
    }

    [Fact]
    public void IssueToken_WrongPassword_Fails()
    {
        var hasher = new PasswordHasher<UserConfig>();
        var user = new UserConfig
        {
            Username = "admin",
            Password = hasher.HashPassword(null!, "admin123"),
            Role = "Admin"
        };

        var token = CreateGenerator(user).IssueToken("admin", "wrong");

        Assert.Null(token);
    }
}
