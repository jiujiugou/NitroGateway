namespace NitroGateway.Transport.HTTP;

/// <summary>HTTP 连接参数</summary>
public sealed record HttpConnectionOptions
{
    /// <summary>基础 URL，如 "https://api.example.com"</summary>
    public required string BaseUrl { get; init; }

    /// <summary>请求超时（毫秒），默认 30 秒</summary>
    public int TimeoutMs { get; init; } = 30_000;

    /// <summary>失败重试次数</summary>
    public int MaxRetries { get; init; } = 3;

    /// <summary>重试退避基数（毫秒）</summary>
    public int RetryBackoffBaseMs { get; init; } = 500;

    /// <summary>认证类型</summary>
    public HttpAuthType AuthType { get; init; } = HttpAuthType.None;

    /// <summary>Bearer Token（AuthType 为 BearerToken 时必填）</summary>
    public string? BearerToken { get; init; }

    /// <summary>健康检查路径，如 "/health"。留空则不做主动健康检查</summary>
    public string? HealthPath { get; init; }
}
