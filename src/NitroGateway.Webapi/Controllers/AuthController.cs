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

    public AuthController(TokenGenerator tokens)
    {
        _tokens = tokens;
    }

    /// <summary>登录并获取 JWT Token</summary>
    [HttpPost("login")]
    public ActionResult<ApiResponse<LoginResponse>> Login([FromBody] LoginRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
            return BadRequest(ApiResponse<LoginResponse>.Fail("Login", "用户名和密码不能为空"));

        var token = _tokens.IssueToken(req.Username, req.Password);
        if (token is null)
            return Unauthorized(ApiResponse<LoginResponse>.Fail("Login", "用户名或密码错误"));

        return Ok(ApiResponse<LoginResponse>.Ok(new LoginResponse
        {
            Token = token,
            TokenType = "Bearer"
        }));
    }
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
