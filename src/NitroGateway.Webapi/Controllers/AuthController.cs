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
    public ActionResult<ApiResponse<LoginResponse>> Login([FromBody] LoginRequest req)
    {
        // ADR-004 P3-2：用户名 Trim，避免首尾空格导致匹配失败
        var username = req.Username?.Trim() ?? "";
        var password = req.Password ?? "";

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            return BadRequest(ApiResponse<LoginResponse>.Fail("Login", "用户名和密码不能为空"));

        // ADR-004 P2-1：失败计数 + 短时锁定，防暴力破解
        var key = BuildKey(username);
        if (_limiter.IsLocked(key, out var remaining))
        {
            return StatusCode(
                StatusCodes.Status429TooManyRequests,
                ApiResponse<LoginResponse>.Fail("Login", $"尝试过于频繁，请 {Math.Ceiling(remaining.TotalSeconds)} 秒后再试"));
        }

        var token = _tokens.IssueToken(username, password);
        if (token is null)
        {
            _limiter.RecordFailure(key);
            return Unauthorized(ApiResponse<LoginResponse>.Fail("Login", "用户名或密码错误"));
        }

        _limiter.Reset(key);
        return Ok(ApiResponse<LoginResponse>.Ok(new LoginResponse
        {
            Token = token,
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
