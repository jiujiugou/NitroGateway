using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NitroGateway.Security.Auth;
using NitroGateway.Webapi.Models;

namespace NitroGateway.Webapi.Controllers;

/// <summary>认证接口</summary>
[ApiController, Route("api/[controller]")]
[AllowAnonymous]
public class AuthController : ControllerBase
{
    private readonly TokenGenerator _tokens;
    private readonly LoginRateLimiter _limiter;

    public AuthController(TokenGenerator tokens, LoginRateLimiter limiter)
    {
        _tokens = tokens;
        _limiter = limiter;
    }

    /// <summary>登录并获取 JWT Token</summary>
    [HttpPost("login")]
    public async Task<ActionResult<ApiResponse<LoginResponse>>> Login(
        [FromBody] LoginRequest req, CancellationToken ct)
    {
        // ADR-004 P3-2：用户名 Trim，避免首尾空格导致匹配失败
        var username = req.Username?.Trim() ?? "";
        var password = req.Password ?? "";

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            return BadRequest(ApiResponse<LoginResponse>.Fail("Login", "用户名和密码不能为空"));

        // ADR-066：用户 DB 化——登录实时读 users 表（改密/启停/新增即时生效，无需改配置重启）
        // ADR-004 P2-1：失败计数 + 短时锁定，防暴力破解
        var key = BuildKey(username);
        if (_limiter.IsLocked(key, out var remaining))
        {
            return StatusCode(
                StatusCodes.Status429TooManyRequests,
                ApiResponse<LoginResponse>.Fail("Login", $"尝试过于频繁，请 {Math.Ceiling(remaining.TotalSeconds)} 秒后再试"));
        }

        var result = await _tokens.IssueTokenAsync(username, password, ct);
        if (!result.IsSuccess)
        {
            // 不区分「用户不存在/密码错误」统一 401，避免泄露账号存在性；停用单独 403 便于管理方感知
            _limiter.RecordFailure(key);
            return result.Status switch
            {
                TokenIssueStatus.Disabled => StatusCode(
                    StatusCodes.Status403Forbidden,
                    ApiResponse<LoginResponse>.Fail("Login", "账号已停用，请联系管理员")),
                _ => Unauthorized(ApiResponse<LoginResponse>.Fail("Login", "用户名或密码错误"))
            };
        }

        _limiter.Reset(key);
        return Ok(ApiResponse<LoginResponse>.Ok(new LoginResponse
        {
            Token = result.Token!,
            TokenType = "Bearer"
        }));
    }

    private string BuildKey(string username)
        => $"{username}|{HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";
}

/// <summary>登录请求</summary>
public class LoginRequest
{
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
}

/// <summary>登录响应</summary>
public class LoginResponse
{
    public string Token { get; set; } = "";
    public string TokenType { get; set; } = "Bearer";
}
