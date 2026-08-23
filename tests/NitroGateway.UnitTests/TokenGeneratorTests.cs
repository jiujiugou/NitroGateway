using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using NitroGateway.Security.Auth;
using NitroGateway.Shared;
using Xunit;

namespace NitroGateway.UnitTests;

/// <summary>
/// ADR-066：TokenGenerator 改读用户存储（DB 化）——密码仅支持哈希校验（ADR-004 P1-2）、
/// 停用账号拒签、用户不存在/密码错误区分状态（对外统一 401）。
/// </summary>
public class TokenGeneratorTests
{
    /// <summary>内存用户存储桩：实现 IUserStore 全契约，支持按用户名查找与刷新 LastLoginAt。</summary>
    private sealed class FakeUserStore : IUserStore
    {
        private readonly List<UserAccount> _users;

        public FakeUserStore(params UserAccount[] users) => _users = users.ToList();

        public Task<UserAccount?> FindByUsernameAsync(string username, CancellationToken ct = default)
            => Task.FromResult(_users.FirstOrDefault(u => u.Username == username));

        public Task<UserAccount?> FindByIdAsync(int id, CancellationToken ct = default)
            => Task.FromResult(_users.FirstOrDefault(u => u.Id == id));

        public Task<IReadOnlyList<UserAccount>> ListAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<UserAccount>>(_users.ToList());

        public Task<OperationResult<UserAccount>> CreateAsync(
            string username, string passwordHash, string role, CancellationToken ct = default)
            => Task.FromResult<OperationResult<UserAccount>>(new UserAccount
            {
                Id = _users.Count + 1,
                Username = username,
                PasswordHash = passwordHash,
                Role = role,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });

        public Task<bool> UpdateRoleAsync(int id, string role, CancellationToken ct = default) => Task.FromResult(true);

        public Task<bool> UpdateEnabledAsync(int id, bool isEnabled, CancellationToken ct = default) => Task.FromResult(true);

        public Task<bool> UpdatePasswordHashAsync(int id, string passwordHash, CancellationToken ct = default) => Task.FromResult(true);

        public Task<bool> UpdateLastLoginAsync(int id, DateTime lastLoginAt, CancellationToken ct = default)
        {
            var user = _users.FirstOrDefault(u => u.Id == id);
            if (user is not null)
                user.LastLoginAt = lastLoginAt;
            return Task.FromResult(user is not null);
        }

        public Task<bool> DeleteAsync(int id, CancellationToken ct = default) => Task.FromResult(true);

        public Task<int> SeedIfEmptyAsync(IReadOnlyList<UserConfig> configUsers, CancellationToken ct = default)
            => Task.FromResult(0);
    }

    private static TokenGenerator CreateGenerator(params UserAccount[] users)
        => new(
            new JwtConfig { JwtSecretKey = new string('K', 64) },
            new FakeUserStore(users),
            new PasswordHasher<UserAccount>(),
            NullLogger<TokenGenerator>.Instance);

    private static string Hash(string plain) => new PasswordHasher<UserAccount>().HashPassword(null!, plain);

    private static UserAccount NewUser(string username = "admin", string? passwordHash = null,
        string role = "Admin", bool enabled = true) => new()
    {
        Id = 1,
        Username = username,
        PasswordHash = passwordHash ?? Hash("admin123"),
        Role = role,
        IsEnabled = enabled,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    [Fact]
    public async Task IssueTokenAsync_HashedPassword_Succeeds_AndUpdatesLastLogin()
    {
        var store = new FakeUserStore(NewUser());
        var generator = new TokenGenerator(
            new JwtConfig { JwtSecretKey = new string('K', 64) },
            store,
            new PasswordHasher<UserAccount>(),
            NullLogger<TokenGenerator>.Instance);

        var result = await generator.IssueTokenAsync("admin", "admin123");

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Token);
        // 成功签发后刷新最近登录时间（管理页展示）
        Assert.NotNull((await store.FindByUsernameAsync("admin"))!.LastLoginAt);
    }

    [Fact]
    public async Task IssueTokenAsync_IssuedToken_CarriesRoleClaim()
    {
        var generator = CreateGenerator(NewUser(role: "Admin"));

        var result = await generator.IssueTokenAsync("admin", "admin123");

        Assert.True(result.IsSuccess);
        var handler = new JwtSecurityTokenHandler();
        var token = handler.ReadJwtToken(result.Token);
        Assert.Equal("admin", token.Claims.First(c => c.Type == ClaimTypes.Name).Value);
        Assert.Equal("Admin", token.Claims.First(c => c.Type == ClaimTypes.Role).Value);
    }

    [Fact]
    public async Task IssueTokenAsync_PlaintextConfiguredPassword_Fails()
    {
        // ADR-004 P1-2：仅支持哈希密码，明文直接校验必然失败（配置层已归一化哈希）
        var generator = CreateGenerator(NewUser(passwordHash: "admin123"));

        var result = await generator.IssueTokenAsync("admin", "admin123");

        Assert.Equal(TokenIssueStatus.InvalidPassword, result.Status);
        Assert.Null(result.Token);
    }

    [Fact]
    public async Task IssueTokenAsync_WrongPassword_Fails()
    {
        var generator = CreateGenerator(NewUser());

        var result = await generator.IssueTokenAsync("admin", "wrong");

        Assert.Equal(TokenIssueStatus.InvalidPassword, result.Status);
        Assert.Null(result.Token);
    }

    [Fact]
    public async Task IssueTokenAsync_UserNotFound_Fails()
    {
        var generator = CreateGenerator(NewUser());

        var result = await generator.IssueTokenAsync("nobody", "admin123");

        Assert.Equal(TokenIssueStatus.UserNotFound, result.Status);
        Assert.Null(result.Token);
    }

    [Fact]
    public async Task IssueTokenAsync_DisabledUser_Fails()
    {
        // ADR-066：停用账号拒绝登录（403），防止禁用后仍可用旧凭据进入
        var generator = CreateGenerator(NewUser(enabled: false));

        var result = await generator.IssueTokenAsync("admin", "admin123");

        Assert.Equal(TokenIssueStatus.Disabled, result.Status);
        Assert.Null(result.Token);
    }
}
