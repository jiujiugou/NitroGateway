using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace NitroGateway.ApiE2eTests;

/// <summary>
/// L3-E：认证与 RBAC 端到端（对应 FACTORY-TEST T0.6 / T5.1~T5.3 的自动化版）。
/// 断言走完整 HTTP + 中间件 + JWT + DB 用户种子链路，而非控制器直调。
/// </summary>
[Collection("E2E")]
public class AuthFlowE2eTests
{
    private readonly E2eApp _app;

    public AuthFlowE2eTests(E2eApp app) => _app = app;

    [Fact]
    public async Task E_Login_Admin_ReturnsBearerToken()
    {
        using var client = _app.CreateAdminClient();
        var resp = await client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "admin123" });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<LoginEnvelope>();
        Assert.True(body!.Success);
        Assert.False(string.IsNullOrWhiteSpace(body.Data?.Token));
        Assert.Equal("Bearer", body.Data.TokenType);
    }

    [Fact]
    public async Task E_Login_WrongPassword_Is401()
    {
        using var client = _app.CreateAdminClient();
        var resp = await client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "wrong-password" });

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task E_Devices_WithoutToken_Is401()
    {
        using var client = _app.CreateAdminClient();
        var resp = await client.GetAsync("/api/devices");

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task E_Operator_CanReadDevices()
    {
        using var client = await _app.LoginAsAsync("operator", "oper123");
        var resp = await client.GetAsync("/api/devices");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }
}
