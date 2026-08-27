using System.Text.Json;
using System.Text.Json.Serialization;

namespace NitroGateway.Command;

/// <summary>
/// 命令回执载荷序列化（契约：commands/ack JSON，camelCase，字段 commandId/result/error/at）。
/// 与云侧 CommandAckParser 对称——result 用枚举名（Success/Failure），at 用 ISO 8601 带偏移（UTC）。
/// </summary>
internal static class CommandAckSerializer
{
    /// <summary>共享序列化选项：camelCase（与云侧契约一致）；只读选项可跨线程并发复用</summary>
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>把回执序列化为 UTF-8 字节（MQTT 载荷）</summary>
    public static byte[] Serialize(Guid commandId, CommandAck ack)
        => JsonSerializer.SerializeToUtf8Bytes(new CommandAckPayload
        {
            CommandId = commandId,
            Result = ack.Result.ToString(),
            Error = ack.Error,
            At = ack.At
        }, Options);
}

/// <summary>命令回执 JSON 载荷（camelCase，字段名与云侧契约一致）</summary>
internal sealed record CommandAckPayload
{
    /// <summary>对应命令 ID（云侧按此做幂等）</summary>
    [JsonPropertyName("commandId")]
    public Guid CommandId { get; init; }

    /// <summary>执行结果（Success/Failure）</summary>
    [JsonPropertyName("result")]
    public string Result { get; init; } = "";

    /// <summary>失败原因（Success 时为空串）</summary>
    [JsonPropertyName("error")]
    public string Error { get; init; } = "";

    /// <summary>回执时间（ISO 8601 带时区偏移，UTC）</summary>
    [JsonPropertyName("at")]
    public DateTimeOffset At { get; init; }
}
