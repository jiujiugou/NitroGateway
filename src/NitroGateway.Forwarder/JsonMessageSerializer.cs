using System.Text.Json;
using NitroGateway.Domain.Measurements;

namespace NitroGateway.Forwarder;

/// <summary>
/// JSON 序列化器：System.Text.Json 实现，v1 默认实现。
/// 属性名采用 camelCase（与前端/云端 JSON 约定一致），输出 UTF-8 字节。
/// </summary>
public sealed class JsonMessageSerializer : IMessageSerializer
{
    /// <summary>
    /// 共享的 JSON 选项（camelCase 命名策略）。
    /// 静态只读、线程安全，可在并发发布间复用，避免每次序列化重复创建配置。
    /// </summary>
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <inheritdoc />
    public string ContentType => "application/json";

    /// <inheritdoc />
    /// <remarks>序列化为 camelCase JSON 后按 UTF-8 编码为字节</remarks>
    public byte[] Serialize(BatchMeasurements batch)
    {
        var json = JsonSerializer.Serialize(batch, Options);
        return System.Text.Encoding.UTF8.GetBytes(json);
    }
}
