using System.Text;
using NitroGateway.Command;
using NitroGateway.Shared;
using Xunit;

namespace NitroGateway.UnitTests;

/// <summary>
/// CommandRequestParser 测试（ADR-069）：下行命令 topic/JSON 解析与契约校验。
/// 覆盖：value 解包（long/double/string/bool）、siteId 不一致、topic 段数/后缀错误、
/// deviceId 非法、类型不支持、缺 commandId/pointId、value 为空、JSON 非法、requestedAt 缺省回退。
/// </summary>
public sealed class CommandRequestParserTests
{
    private const string SiteId = "site-a";
    private static readonly Guid DeviceId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid PointId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid CommandId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static string Topic(string siteId = SiteId, string deviceId = null!)
        => $"nitrogateway/{siteId}/{deviceId ?? DeviceId.ToString()}/commands";

    private static byte[] Payload(string json) => Encoding.UTF8.GetBytes(json);

    private static string BaseJson(string type = "WritePoint", string? value = "42", bool withRequestedAt = true)
        => $"{{\"commandId\":\"{CommandId}\",\"type\":\"{type}\",\"pointId\":\"{PointId}\",\"value\":{value}" +
           (withRequestedAt ? ",\"requestedAt\":\"2026-08-27T08:00:00+08:00\"" : "") + "}";

    [Fact]
    public void Parse_NumberValue_UnwrapsToLong()
    {
        var r = CommandRequestParser.Parse(Topic(), Payload(BaseJson(value: "42")), SiteId);
        Assert.True(r.IsSuccess, r.Error?.Message);
        Assert.Equal(42L, r.Value!.Value);
    }

    [Fact]
    public void Parse_DoubleValue_UnwrapsToDouble()
    {
        var r = CommandRequestParser.Parse(Topic(), Payload(BaseJson(value: "3.14")), SiteId);
        Assert.True(r.IsSuccess, r.Error?.Message);
        Assert.Equal(3.14, r.Value!.Value);
    }

    [Fact]
    public void Parse_StringValue_UnwrapsToString()
    {
        var r = CommandRequestParser.Parse(Topic(), Payload(BaseJson(value: "\"abc\"")), SiteId);
        Assert.True(r.IsSuccess, r.Error?.Message);
        Assert.Equal("abc", r.Value!.Value);
    }

    [Fact]
    public void Parse_BoolValue_UnwrapsToBool()
    {
        var r = CommandRequestParser.Parse(Topic(), Payload(BaseJson(value: "true")), SiteId);
        Assert.True(r.IsSuccess, r.Error?.Message);
        Assert.Equal(true, r.Value!.Value);
    }

    [Fact]
    public void Parse_SiteIdMismatch_ReturnsValidationFailure()
    {
        var r = CommandRequestParser.Parse(Topic(siteId: "site-b"), Payload(BaseJson()), SiteId);
        Assert.True(r.IsFailure);
        Assert.Equal("ValidationError", r.Error?.Code);
    }

    [Fact]
    public void Parse_TooManySegments_ReturnsProtocolFailure()
    {
        var r = CommandRequestParser.Parse($"{Topic()}/extra", Payload(BaseJson()), SiteId);
        Assert.True(r.IsFailure);
        Assert.Equal("ProtocolError", r.Error?.Code);
    }

    [Fact]
    public void Parse_WrongSuffix_ReturnsProtocolFailure()
    {
        var r = CommandRequestParser.Parse($"nitrogateway/{SiteId}/{DeviceId}/telemetry", Payload(BaseJson()), SiteId);
        Assert.True(r.IsFailure);
        Assert.Equal("ProtocolError", r.Error?.Code);
    }

    [Fact]
    public void Parse_InvalidDeviceId_ReturnsProtocolFailure()
    {
        var r = CommandRequestParser.Parse($"nitrogateway/{SiteId}/not-a-guid/commands", Payload(BaseJson()), SiteId);
        Assert.True(r.IsFailure);
        Assert.Equal("ProtocolError", r.Error?.Code);
    }

    [Fact]
    public void Parse_UnsupportedType_ReturnsProtocolFailure()
    {
        var r = CommandRequestParser.Parse(Topic(), Payload(BaseJson(type: "ReadPoint")), SiteId);
        Assert.True(r.IsFailure);
        Assert.Equal("ProtocolError", r.Error?.Code);
    }

    [Fact]
    public void Parse_MissingCommandId_ReturnsProtocolFailure()
    {
        var json = $"{{\"type\":\"WritePoint\",\"pointId\":\"{PointId}\",\"value\":42}}";
        var r = CommandRequestParser.Parse(Topic(), Payload(json), SiteId);
        Assert.True(r.IsFailure);
        Assert.Equal("ProtocolError", r.Error?.Code);
    }

    [Fact]
    public void Parse_MissingPointId_ReturnsProtocolFailure()
    {
        var json = $"{{\"commandId\":\"{CommandId}\",\"type\":\"WritePoint\",\"value\":42}}";
        var r = CommandRequestParser.Parse(Topic(), Payload(json), SiteId);
        Assert.True(r.IsFailure);
        Assert.Equal("ProtocolError", r.Error?.Code);
    }

    [Fact]
    public void Parse_NullValue_ReturnsValidationFailure()
    {
        var r = CommandRequestParser.Parse(Topic(), Payload(BaseJson(value: "null")), SiteId);
        Assert.True(r.IsFailure);
        Assert.Equal("ValidationError", r.Error?.Code);
    }

    [Fact]
    public void Parse_InvalidJson_ReturnsProtocolFailure()
    {
        var r = CommandRequestParser.Parse(Topic(), Payload("not json"), SiteId);
        Assert.True(r.IsFailure);
        Assert.Equal("ProtocolError", r.Error?.Code);
    }

    [Fact]
    public void Parse_MissingRequestedAt_FallsBackToUtcNow()
    {
        var before = DateTimeOffset.UtcNow;
        var r = CommandRequestParser.Parse(Topic(), Payload(BaseJson(withRequestedAt: false)), SiteId);
        Assert.True(r.IsSuccess, r.Error?.Message);
        Assert.InRange(r.Value!.RequestedAt, before.AddSeconds(-1), DateTimeOffset.UtcNow.AddSeconds(1));
    }
}
