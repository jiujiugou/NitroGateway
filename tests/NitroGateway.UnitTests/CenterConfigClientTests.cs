using System.Net;
using System.Text;
using System.Text.Json;
using System.Globalization;
using NitroGateway.Desktop.Services;
using NitroGateway.Domain.Devices;
using NitroGateway.Shared;
using Xunit;

namespace NitroGateway.UnitTests;

/// <summary>
/// ADR-033 阶段 2：中心快照客户端——请求构造（URL + Bearer）、响应解析、
/// 鉴权失败/网络不可达/响应异常的错误归类。
/// </summary>
public sealed class CenterConfigClientTests
{
    [Fact]
    public async Task FetchSnapshot_sends_request_with_bearer_and_parses_devices()
    {
        var deviceId = Guid.NewGuid();
        var pointId = Guid.NewGuid();
        var handler = new StubHttpMessageHandler(req =>
        {
            Assert.EndsWith("/api/devices/export", req.RequestUri!.AbsoluteUri);
            Assert.Equal(HttpMethod.Get, req.Method);
            Assert.Equal("Bearer tok123", req.Headers.Authorization!.ToString());
            return Json("""{"success":true,"data":[{"id":"DEVICEID","name":"PLC-1","description":null,"protocol":{"name":"Modbus","dialect":"TCP"},"connection":{"endpoint":"192.168.1.10:502","connectTimeoutMs":3000,"requestTimeoutMs":5000,"retryCount":3,"retryIntervalMs":1000,"parameters":{"unitId":1}},"status":"Online","points":[{"id":"POINTID","name":"炉温","address":"40001","description":null,"dataType":"Float","access":"ReadOnly","enabled":true,"scanIntervalMs":1000,"deadband":0.0,"scaleFactor":1.0,"scaleOffset":0.0}]}],"error":null,"timestamp":"2026-08-12T00:00:00Z"}"""
                .Replace("DEVICEID", deviceId.ToString()).Replace("POINTID", pointId.ToString()));
        });
        var client = new CenterConfigClient(handler);

        var result = await client.FetchSnapshotAsync("http://center.example.com:5100/", " tok123 ");

        Assert.True(result.IsSuccess, result.Error?.Message);
        var device = Assert.Single(result.Value!);
        Assert.Equal(deviceId, device.Id);
        Assert.Equal("PLC-1", device.Name);
        Assert.Equal("Modbus", device.Protocol.Name);
        Assert.Equal("TCP", device.Protocol.Dialect);
        Assert.Equal("192.168.1.10:502", device.Connection.Endpoint);
        // Parameters 反序列化为 JsonElement（System.Text.Json 字典值），取整型比较
        Assert.Equal(1, ((JsonElement)device.Connection.Parameters["unitId"]).GetInt32());
        // ADR-029：状态不伪造，由 HealthMonitor 驱动
        Assert.Equal(DeviceStatus.Unknown, device.Status);
        var point = Assert.Single(device.Points);
        Assert.Equal(pointId, point.Id);
        Assert.Equal("炉温", point.Name);
        Assert.Equal("40001", point.Address);
        Assert.Equal(DataType.Float, point.DataType);
        Assert.Equal(PointAccess.ReadOnly, point.Access);
    }

    [Fact]
    public async Task FetchSnapshot_unauthorized_returns_auth_error()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        });
        var client = new CenterConfigClient(handler);

        var result = await client.FetchSnapshotAsync("http://center.example.com:5100", "bad-token");

        Assert.True(result.IsFailure);
        Assert.Contains("鉴权失败", result.Error!.Message);
    }

    [Fact]
    public async Task FetchSnapshot_unreachable_returns_connection_error()
    {
        var handler = new StubHttpMessageHandler(_ => throw new HttpRequestException("connection refused"));
        var client = new CenterConfigClient(handler);

        var result = await client.FetchSnapshotAsync("http://center.example.com:5100", "");

        Assert.True(result.IsFailure);
        Assert.Contains("无法连接中心", result.Error!.Message);
    }

    [Fact]
    public async Task FetchSnapshot_business_failure_returns_format_error()
    {
        var handler = new StubHttpMessageHandler(_ => Json("""{"success":false,"data":null,"error":{"code":"GetAll","message":"boom"}}"""));
        var client = new CenterConfigClient(handler);

        var result = await client.FetchSnapshotAsync("http://center.example.com:5100", "");

        Assert.True(result.IsFailure);
        Assert.Contains("响应格式不正确", result.Error!.Message);
    }

    [Fact]
    public async Task FetchSnapshot_empty_center_url_returns_validation_error()
    {
        var client = new CenterConfigClient(new StubHttpMessageHandler(_ => throw new InvalidOperationException()));

        var result = await client.FetchSnapshotAsync("   ", "");

        Assert.True(result.IsFailure);
        Assert.Contains("中心地址不能为空", result.Error!.Message);
    }

    [Fact]
    public async Task FetchSyncSnapshot_parses_tombstones_timestamps_and_server_time()
    {
        var deviceId = Guid.NewGuid();
        var pointId = Guid.NewGuid();
        var updatedAt = "2026-08-12T06:00:00.0000000Z";
        var serverTime = "2026-08-12T07:00:00.0000000Z";
        var handler = new StubHttpMessageHandler(req =>
        {
            Assert.EndsWith("/api/configsync/export", req.RequestUri!.AbsoluteUri);
            Assert.Equal(HttpMethod.Get, req.Method);
            Assert.Equal("Bearer tok123", req.Headers.Authorization!.ToString());
            return Json($$"""
                {"success":true,"data":{"serverTime":"SERVERTIME","devices":[
                  {"id":"DEVICEID","name":"PLC-1","description":null,"protocol":{"name":"Modbus","dialect":"TCP"},
                   "connection":{"endpoint":"192.168.1.10:502"},"status":"Online","updatedAt":"UPDATEDAT","isDeleted":true,
                   "points":[{"id":"POINTID","name":"炉温","address":"40001","dataType":"Float","access":"ReadOnly",
                              "enabled":true,"updatedAt":"UPDATEDAT","isDeleted":true}]}
                ]},"error":null,"timestamp":"2026-08-12T07:00:00Z"}
                """.Replace("SERVERTIME", serverTime).Replace("DEVICEID", deviceId.ToString()).Replace("POINTID", pointId.ToString())
                .Replace("UPDATEDAT", updatedAt));
        });
        var client = new CenterConfigClient(handler);

        var result = await client.FetchSyncSnapshotAsync("http://center.example.com:5100", " tok123 ");

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(ParseUtc(serverTime), result.Value!.ServerTime);
        var device = Assert.Single(result.Value!.Devices);
        Assert.Equal(deviceId, device.Id);
        Assert.True(device.IsDeleted);
        Assert.Equal(ParseUtc(updatedAt), device.UpdatedAt);
        var point = Assert.Single(device.Points);
        Assert.True(point.IsDeleted);
        Assert.Equal(ParseUtc(updatedAt), point.UpdatedAt);
    }

    private static DateTime ParseUtc(string value)
        => DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    [Fact]
    public async Task PushChanges_serializes_payload_and_parses_results()
    {
        var deviceId = Guid.NewGuid();
        var deletedPointId = Guid.NewGuid();
        var device = TestDevices.Device("PLC-1");
        device.UpdatedAt = DateTime.UtcNow.AddMinutes(-5);
        var handler = new StubHttpMessageHandler(req =>
        {
            Assert.EndsWith("/api/configsync/push", req.RequestUri!.AbsoluteUri);
            Assert.Equal(HttpMethod.Post, req.Method);
            Assert.Equal("Bearer tok123", req.Headers.Authorization!.ToString());
            var body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            var root = doc.RootElement;
            Assert.Equal("site-a", root.GetProperty("siteId").GetString());
            var changes = root.GetProperty("changes");
            Assert.Equal(2, changes.GetArrayLength());

            var tombstone = changes[0];
            Assert.True(tombstone.GetProperty("deleted").GetBoolean());
            Assert.Equal(deviceId.ToString(), tombstone.GetProperty("deviceId").GetString());

            var upsert = changes[1];
            Assert.False(upsert.GetProperty("deleted").GetBoolean());
            Assert.Equal("PLC-1", upsert.GetProperty("device").GetProperty("name").GetString());
            Assert.Equal(deletedPointId.ToString(),
                Assert.Single(upsert.GetProperty("deletedPointIds").EnumerateArray()).GetString());
            return Json("""
                {"success":true,"data":{"results":[
                    {"deviceId":"DEVICEID","action":"accepted"},
                    {"deviceId":"DEVICEID","action":"accepted"}
                ]},"error":null,"timestamp":"2026-08-12T07:00:00Z"}
                """.Replace("DEVICEID", deviceId.ToString()));
        });
        var client = new CenterConfigClient(handler);

        var result = await client.PushChangesAsync(
            "http://center.example.com:5100", " tok123 ", "site-a",
            [
                new CenterSyncChange(deviceId, null, Deleted: true, []),
                new CenterSyncChange(deviceId, device, Deleted: false, [deletedPointId])
            ]);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(2, result.Value!.Count);
        Assert.All(result.Value!, r => Assert.Equal("accepted", r.Action));
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    /// <summary>测试用 HttpMessageHandler：记录请求并按响应器返回（不真正出网）。</summary>
    internal sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(_responder(request));
    }
}
