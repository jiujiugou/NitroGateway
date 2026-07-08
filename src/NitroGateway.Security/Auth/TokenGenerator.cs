using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace NitroGateway.Security.Auth;

/// <summary>
/// JWT Token 签发器。薄封装 JwtSecurityTokenHandler。
/// </summary>
public sealed class TokenGenerator
{
    private readonly JwtConfig _config;
    private readonly IReadOnlyList<UserConfig> _users;
    private readonly PasswordHasher<UserConfig> _hasher = new();
    private readonly ILogger<TokenGenerator> _logger;

    public TokenGenerator(
        JwtConfig config,
        IEnumerable<UserConfig> users,
        ILogger<TokenGenerator> logger)
    {
        _config = config;
        _users = users.ToList();
        _logger = logger;
    }

    /// <summary>验证用户名密码并签发 Token。失败返回 null</summary>
    public string? IssueToken(string username, string password)
    {
        var user = _users.FirstOrDefault(u =>
            string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase));

        if (user is null)
        {
            _logger.LogWarning("登录失败: 用户 {User} 不存在", username);
            return null;
        }

        // 支持明文和哈希两种密码格式
        var verifyResult = _hasher.VerifyHashedPassword(user, user.Password, password);
        if (verifyResult != PasswordVerificationResult.Success &&
            !string.Equals(user.Password, password, StringComparison.Ordinal))
        {
            _logger.LogWarning("登录失败: 用户 {User} 密码错误", username);
            return null;
        }

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_config.SecretKey));
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

        _logger.LogInformation("Token 签发: 用户 {User}, 角色 {Role}", username, user.Role);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
