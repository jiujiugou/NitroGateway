using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace NitroGateway.ApiE2eTests;

/// <summary>
/// L3-E：设备/点位 CRUD 端到端（对应 smoke-test.sh 的链路 + FACTORY-TEST T1.1~T1.3 前半）。
/// 走真 Webapi → Controller → DeviceManager/PointManager → SQLite 仓储，断言能读回落库结果。
/// </summary>
[Collection("E2E")]
public class DeviceCrudE2eTests
{
    private readonly E2eApp _app;

    public DeviceCrudE2eTests(E2eApp app) => _app = app;

    private static object ModbusDevice(string name) => new
    {
        name,
        protocol = new { name = "Modbus", dialect = "TCP" },
        connection = new { endpoint = "127.0.0.1:502" }
    };

    private static object TempPoint(string address = "40001") => new
    {
        name = $"Temp_{Guid.NewGuid():N}".Substring(0, 16),
        address,
        dataType = "Float",
        access = "ReadWrite"
    };

    private async Task<JsonElement> CreateDeviceAsync(HttpClient client, string name)
    {
        var resp = await client.PostAsJsonAsync("/api/devices", ModbusDevice(name));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
        return doc.RootElement.GetProperty("data").Clone();
    }

    [Fact]
    public async Task E_Device_Create_List_Get_Export()
    {
        using var admin = await _app.LoginAsAsync("admin", "admin123");
        var name = $"PLC-E2E-{Guid.NewGuid():N}".Substring(0, 24);
        var created = await CreateDeviceAsync(admin, name);
        var id = created.GetProperty("id").GetString()!;
        Assert.Equal(name, created.GetProperty("name").GetString());
        Assert.Equal("Modbus", created.GetProperty("protocol").GetProperty("name").GetString());

        // 读回（SQLite 落库证据）
        var list = await admin.GetAsync("/api/devices");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var listText = await list.Content.ReadAsStringAsync();
        Assert.Contains(id, listText);

        var one = await admin.GetAsync($"/api/devices/{id}");
        Assert.Equal(HttpStatusCode.OK, one.StatusCode);
        var oneText = await one.Content.ReadAsStringAsync();
        Assert.Contains(name, oneText);

        // 只读导出（ADR-033 阶段 2 契约）
        var export = await admin.GetAsync("/api/devices/export");
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        Assert.Contains(id, await export.Content.ReadAsStringAsync());

        // 清理
        Assert.Equal(HttpStatusCode.OK, (await admin.DeleteAsync($"/api/devices/{id}")).StatusCode);
    }

    [Fact]
    public async Task E_Device_AddPoint_ThenQueryHistory_ReturnsOk()
    {
        using var admin = await _app.LoginAsAsync("admin", "admin123");
        var created = await CreateDeviceAsync(admin, $"PLC-E2E-{Guid.NewGuid():N}".Substring(0, 24));
        var deviceId = created.GetProperty("id").GetString()!;

        var point = TempPoint();
        var addResp = await admin.PostAsJsonAsync($"/api/devices/{deviceId}/points", point);
        var addBody = await addResp.Content.ReadAsStringAsync();
        Assert.True(addResp.StatusCode == HttpStatusCode.OK,
            $"AddPoint 未返回 200: {(int)addResp.StatusCode} {addBody}");
        using var addDoc = JsonDocument.Parse(addBody);
        var pointId = addDoc.RootElement.GetProperty("data").GetProperty("id").GetString()!;

        // 无模拟器时无数据 → 断言契约仍是 200 + success（采集链路由 L2/FACTORY-T1 覆盖）
        var history = await admin.GetAsync(
            $"/api/measurements/history?deviceId={deviceId}&pointId={pointId}&from=2020-01-01T00:00:00Z&to=2030-01-01T00:00:00Z");
        var historyBody = await history.Content.ReadAsStringAsync();
        Assert.True(history.StatusCode == HttpStatusCode.OK,
            $"history 未返回 200: {(int)history.StatusCode} {historyBody}");
        using var hDoc = JsonDocument.Parse(historyBody);
        Assert.True(hDoc.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(JsonValueKind.Array, hDoc.RootElement.GetProperty("data").ValueKind);

        // 清理
        Assert.Equal(HttpStatusCode.OK, (await admin.DeleteAsync($"/api/devices/{deviceId}")).StatusCode);
    }

    [Fact]
    public async Task E_Device_AddPoint_InvalidDataType_Is400()
    {
        using var admin = await _app.LoginAsAsync("admin", "admin123");
        var created = await CreateDeviceAsync(admin, $"PLC-E2E-{Guid.NewGuid():N}".Substring(0, 24));
        var deviceId = created.GetProperty("id").GetString()!;

        var resp = await admin.PostAsJsonAsync($"/api/devices/{deviceId}/points",
            new { name = "Bad", address = "40001", dataType = "NotAType" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        await admin.DeleteAsync($"/api/devices/{deviceId}");
    }

    [Fact]
    public async Task E_Viewer_DeleteDevice_Is403()
    {
        // FACTORY-TEST T5.1 自动化版：viewer 越权写 → 403
        using var admin = await _app.LoginAsAsync("admin", "admin123");
        var created = await CreateDeviceAsync(admin, $"PLC-E2E-{Guid.NewGuid():N}".Substring(0, 24));
        var deviceId = created.GetProperty("id").GetString()!;

        using var viewer = await _app.LoginAsAsync("viewer", "view123");
        var resp = await viewer.DeleteAsync($"/api/devices/{deviceId}");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);

        // viewer 可读（只读角色仍能看）
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/api/devices")).StatusCode);

        // 清理
        Assert.Equal(HttpStatusCode.OK, (await admin.DeleteAsync($"/api/devices/{deviceId}")).StatusCode);
    }
}
