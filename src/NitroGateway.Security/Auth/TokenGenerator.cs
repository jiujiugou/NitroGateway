using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace NitroGateway.Security.Auth;

/// <summary>
/// JWT Token 签发器（ADR-066：改读用户存储，不再读配置文件）。薄封装 JwtSecurityTokenHandler。
/// 登录每次从 <see cref="IUserStore"/> 实时读取账号，保证「新增/改密/启停」即时生效无需重启；
/// JWT 签发、RBAC 策略、登录限流行为不变。
/// </summary>
public sealed class TokenGenerator
{
    private readonly JwtConfig _config;
    private readonly IUserStore _store;
    private readonly PasswordHasher<UserAccount> _hasher;
    private readonly ILogger<TokenGenerator> _logger;

    /// <param name="config">JWT 签发配置</param>
    /// <param name="store">用户存储（SQLite 实现，Dapper 单例）</param>
    /// <param name="hasher">PasswordHasher（与配置用户哈希格式兼容，首启种子可直读）</param>
    /// <param name="logger">日志</param>
    public TokenGenerator(
        JwtConfig config,
        IUserStore store,
        PasswordHasher<UserAccount> hasher,
        ILogger<TokenGenerator> logger)
    {
        _config = config;
        _store = store;
        _hasher = hasher;
        _logger = logger;
    }

    /// <summary>
    /// 验证用户名密码并签发 Token（每次从用户存储实时读取）。
    /// 失败返回对应 <see cref="TokenIssueStatus"/>（UserNotFound/InvalidPassword 对外统一 401，Disabled 单独 403）。
    /// </summary>
    public async Task<TokenIssueResult> IssueTokenAsync(
        string username, string password, CancellationToken ct = default)
    {
        var user = await _store.FindByUsernameAsync(username, ct);

        if (user is null)
        {
            _logger.LogWarning("登录失败: 用户 {User} 不存在", username);
            return new TokenIssueResult(TokenIssueStatus.UserNotFound);
        }

        // ADR-066：停用账号拒绝登录（403），防止禁用后仍可用旧凭据进入
        if (!user.IsEnabled)
        {
            _logger.LogWarning("登录失败: 用户 {User} 已停用", username);
            return new TokenIssueResult(TokenIssueStatus.Disabled);
        }

        // ADR-004 P1-2：仅支持哈希密码（PasswordHasher 生成），已移除明文 Equals 回退
        var verifyResult = _hasher.VerifyHashedPassword(user, user.PasswordHash, password);
        if (verifyResult == PasswordVerificationResult.Failed)
        {
            _logger.LogWarning("登录失败: 用户 {User} 密码错误", username);
            return new TokenIssueResult(TokenIssueStatus.InvalidPassword);
        }

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_config.JwtSecretKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(ClaimTypes.Name, username),
            new Claim(ClaimTypes.Role, user.Role),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        var token = new JwtSecurityToken(
            issuer: _config.Issuer,
            audience: _config.Audience,
            claims: claims,
            expires: DateTime.UtcNow.AddHours(_config.ExpireHours),
            signingCredentials: credentials);

        // 刷新最近登录时间（best-effort：失败仅记日志，不阻断签发——登录主流程不受影响）
        try
        {
            await _store.UpdateLastLoginAsync(user.Id, DateTime.UtcNow, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("更新最近登录时间失败（不影响签发）: {Error}", ex.Message);
        }

        _logger.LogInformation("Token 签发: 用户 {User}, 角色 {Role}", username, user.Role);
        return TokenIssueResult.Ok(new JwtSecurityTokenHandler().WriteToken(token));
    }
}
