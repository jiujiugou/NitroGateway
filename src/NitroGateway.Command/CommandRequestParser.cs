using System.Text.Json;
using System.Text.Json.Serialization;
using NitroGateway.Shared;

namespace NitroGateway.Command;

/// <summary>
/// 下行命令 topic + 载荷解析（契约：commands JSON，camelCase，字段 commandId/type/pointId/value/requestedAt）。
/// 职责边界：只做「topic/JSON → <see cref="GatewayCommand"/>」转换与契约校验；写值与回执在 <see cref="CommandProcessor"/>。
/// 解析失败返回 <see cref="OperationalError"/>（Protocol/Validation），不抛异常。
/// </summary>
public static class CommandRequestParser
{
    /// <summary>topic 根段（与云侧契约一致）</summary>
    public const string Root = "nitrogateway";

    /// <summary>命令类型常量：写点位（当前唯一支持类型）</summary>
    public const string WritePointType = "WritePoint";

    /// <summary>共享 JSON 反序列化选项：camelCase；value 用 JsonElement 承载原始 JSON 值</summary>
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// 解析命令。校验：topic 4 段且第 4 段为 commands、siteId 与本地一致、deviceId 合法、
    /// commandId 非空、type 为 WritePoint、pointId 非空、value 非空。
    /// </summary>
    /// <param name="topic">命令 topic（nitrogateway/{siteId}/{deviceId}/commands）</param>
    /// <param name="payload">MQTT 载荷字节（UTF-8 JSON）</param>
    /// <param name="localSiteId">本地站点标识（Site:Id，缺省回退 default）</param>
    public static OperationResult<GatewayCommand> Parse(string topic, ReadOnlySpan<byte> payload, string localSiteId)
    {
        // ── topic 校验 ──
        var parts = topic.Split('/');
        if (parts.Length != 4 || parts[0] != Root || parts[3] != "commands")
            return OperationalError.Protocol($"命令 topic 非法: {topic}");
        if (!string.Equals(parts[1], localSiteId, StringComparison.Ordinal))
            return OperationalError.Validation($"命令 siteId 与本地不一致: {parts[1]} != {localSiteId}");
        if (!Guid.TryParse(parts[2], out var deviceId))
            return OperationalError.Protocol($"命令 topic deviceId 非法: {parts[2]}");

        // ── 载荷解析与校验 ──
        try
        {
            var p = JsonSerializer.Deserialize<CommandRequestPayload>(payload, Options);
            if (p is null)
                return OperationalError.Protocol("命令载荷为空或不是合法 JSON");
            if (p.CommandId == Guid.Empty)
                return OperationalError.Protocol("命令缺少 commandId");
            if (!string.Equals(p.Type, WritePointType, StringComparison.Ordinal))
                return OperationalError.Protocol($"不支持的命令类型: {p.Type}");
            if (p.PointId == Guid.Empty)
                return OperationalError.Protocol("命令缺少 pointId");
            if (p.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                return OperationalError.Validation("命令 value 不能为空");

            return new GatewayCommand
            {
                CommandId = p.CommandId,
                Type = p.Type,
                SiteId = parts[1],
                DeviceId = deviceId,
                PointId = p.PointId,
                Value = Unwrap(p.Value),
                RequestedAt = p.RequestedAt == default ? DateTimeOffset.UtcNow : p.RequestedAt
            };
        }
        catch (JsonException ex)
        {
            return OperationalError.Protocol($"命令载荷 JSON 解析失败: {ex.Message}");
        }
        catch (Exception ex)
        {
            return OperationalError.Protocol($"命令解析异常: {ex.Message}");
        }
    }

    /// <summary>
    /// 把 JsonElement 解包为 CLR 原始类型（number → long/double、string → string、bool → bool），
    /// 与 WriteService 内部 Unwrap 语义一致，避免 JsonElement 生命周期问题。
    /// </summary>
    private static object Unwrap(JsonElement je) => je.ValueKind switch
    {
        JsonValueKind.String => je.GetString() ?? "",
        // 三目两侧显式 object 化：若只写 l / je.GetDouble()，公共类型为 double，整数值会被装箱成 double。
        JsonValueKind.Number => je.TryGetInt64(out var l) ? (object)l : (object)je.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => je.GetRawText()
    };
}

/// <summary>下行命令 JSON 载荷（camelCase，字段名与云侧契约一致；value 保留原始 JSON）</summary>
internal sealed record CommandRequestPayload
{
    /// <summary>命令 ID</summary>
    [JsonPropertyName("commandId")]
    public Guid CommandId { get; init; }

    /// <summary>命令类型（WritePoint）</summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = "";

    /// <summary>目标点位 ID</summary>
    [JsonPropertyName("pointId")]
    public Guid PointId { get; init; }

    /// <summary>写入值（原始 JSON，不限定类型）</summary>
    [JsonPropertyName("value")]
    public JsonElement Value { get; init; }

    /// <summary>云侧发起时间（ISO 8601 带偏移）</summary>
    [JsonPropertyName("requestedAt")]
    public DateTimeOffset RequestedAt { get; init; }
}
