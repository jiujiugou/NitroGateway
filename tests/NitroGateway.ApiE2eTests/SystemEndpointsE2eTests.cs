using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace NitroGateway.ApiE2eTests;

/// <summary>
/// L3-E：系统级端点端到端（FACTORY-TEST T0.5 自动化版）：healthz 200、Swagger（开发态）可达、状态页鉴权可用。
/// </summary>
[Collection("E2E")]
public class SystemEndpointsE2eTests
{
    private readonly E2eApp _app;

    public SystemEndpointsE2eTests(E2eApp app) => _app = app;

    [Fact]
    public async Task E_Healthz_Returns200_WithoutAuth()
    {
        using var client = _app.CreateAdminClient();
        var resp = await client.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task E_Swagger_Available_InDevelopment()
    {
        using var client = _app.CreateAdminClient();
        var resp = await client.GetAsync("/swagger/v1/swagger.json");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var text = await resp.Content.ReadAsStringAsync();
        Assert.Contains("NitroGateway API", text);
    }

    [Fact]
    public async Task E_SystemStatus_Authenticated_IsOk()
    {
        using var operatorClient = await _app.LoginAsAsync("operator", "oper123");
        var resp = await operatorClient.GetAsync("/api/status/system");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
    }
}
