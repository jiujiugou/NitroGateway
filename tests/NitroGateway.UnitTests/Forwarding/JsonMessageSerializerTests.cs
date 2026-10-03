using System.Text;
using System.Text.Json;
using NitroGateway.Domain.Measurements;
using NitroGateway.Forwarder;
using Xunit;

namespace NitroGateway.UnitTests.Forwarding;

/// <summary>JsonMessageSerializer：内容类型与 camelCase UTF-8 JSON 输出。</summary>
public class JsonMessageSerializerTests
{
    [Fact]
    public void ContentType_IsApplicationJson()
        => Assert.Equal("application/json", new JsonMessageSerializer().ContentType);

    [Fact]
    public void Serialize_ProducesCamelCaseUtf8Json()
    {
        var batch = new BatchMeasurements { Id = Guid.NewGuid(), DeviceId = Guid.NewGuid() };

        var bytes = new JsonMessageSerializer().Serialize(batch);
        var json = Encoding.UTF8.GetString(bytes);

        Assert.Contains("\"deviceId\"", json);
        Assert.Contains("\"scanStartedAt\"", json);
        Assert.DoesNotContain("\"DeviceId\"", json);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(batch.Id, doc.RootElement.GetProperty("id").GetGuid());
        Assert.Equal(batch.DeviceId, doc.RootElement.GetProperty("deviceId").GetGuid());
    }
}
