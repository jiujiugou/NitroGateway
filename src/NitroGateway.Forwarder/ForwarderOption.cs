using NitroGateway.Storage.Buffer;

namespace NitroGateway.Forwarder;

/// <summary>
/// 转发模块配置（appsettings 的 "Forwarder" 段，ADR-011 多通道）。
/// Channels 取值：mqtt | http | both（默认 mqtt，行为与旧版一致）。
/// </summary>
public sealed class ForwarderOption
{
    /// <summary>配置节名（appsettings 中为 "Forwarder"）</summary>
    public const string SectionName = "Forwarder";

    /// <summary>转发引擎轮询间隔（毫秒），默认 5000</summary>
    public int IntervalMs { get; init; } = 5000;

    /// <summary>启用通道：mqtt / http / both（大小写不敏感）</summary>
    public string Channels { get; init; } = "mqtt";

    /// <summary>HTTP 通道参数（Channels 含 http 时必填 BaseUrl）</summary>
    public HttpForwarderOption Http { get; init; } = new();

    /// <summary>
    /// 解析启用的北向通道列表（ADR-011 P2/P3，供转发引擎注册与入队路由共用）。
    /// 取值：mqtt / http / both（大小写不敏感）；非法值抛 <see cref="ArgumentException"/>，
    /// 启动即报错，避免运行期静默降级为 mqtt 造成数据不进 http 队列。
    /// </summary>
    public IReadOnlyList<string> ResolveChannels()
    {
        return Channels.Trim().ToLowerInvariant() switch
        {
            "mqtt" => [IForwardBuffer.MqttChannel],
            "http" => [IForwardBuffer.HttpChannel],
            "both" => [IForwardBuffer.MqttChannel, IForwardBuffer.HttpChannel],
            var other => throw new ArgumentException(
                $"Forwarder:Channels 取值必须为 mqtt/http/both，实际为: {other}")
        };
    }
}

/// <summary>HTTP 北向通道参数（ADR-011 P4，映射到 <c>HttpConnectionOptions</c>）</summary>
public sealed class HttpForwarderOption
{
    /// <summary>云端 HTTP 基础 URL，如 "https://center.example.com"（启用 http 通道时必填）</summary>
    public string BaseUrl { get; init; } = "";

    /// <summary>批次上传路径，默认 "/api/measurements/batch"（POST，BatchMeasurements JSON）</summary>
    public string? Path { get; init; } = "/api/measurements/batch";

    /// <summary>认证类型（None / BearerToken）</summary>
    public Transport.HTTP.HttpAuthType AuthType { get; init; }

    /// <summary>Bearer Token（AuthType 为 BearerToken 时填写）</summary>
    public string? BearerToken { get; init; }

    /// <summary>请求超时（毫秒），默认 30 秒</summary>
    public int TimeoutMs { get; init; } = 30_000;

    /// <summary>失败重试次数，默认 3</summary>
    public int MaxRetries { get; init; } = 3;

    /// <summary>健康检查路径，默认 "/health"</summary>
    public string? HealthPath { get; init; } = "/health";
}
