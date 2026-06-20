using System.Text.Json;
using NitroGateway.Domain.Measurements;

namespace NitroGateway.Forwarder;

/// <summary>JSON 序列化器。v1 默认实现</summary>
public sealed class JsonMessageSerializer : IMessageSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <inheritdoc />
    public string ContentType => "application/json";

    /// <inheritdoc />
    public byte[] Serialize(BatchMeasurements batch)
    {
        var json = JsonSerializer.Serialize(batch, Options);
        return System.Text.Encoding.UTF8.GetBytes(json);
    }
}
