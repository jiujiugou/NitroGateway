namespace NitroGateway.Transport.HTTP;

/// <summary>HTTP 连接参数。ADR-020 P3-2：数值项在属性层夹紧，非法配置构造即收敛到合法值。</summary>
public sealed record HttpConnectionOptions
{
    /// <summary>基础 URL，如 "https://api.example.com"</summary>
    public required string BaseUrl { get; init; }

    private int _timeoutMs = 30_000;

    /// <summary>请求超时（毫秒），默认 30 秒；夹紧到 ≥1（HttpClient.Timeout 要求正数）</summary>
    public int TimeoutMs
    {
        get => _timeoutMs;
        init => _timeoutMs = Math.Max(1, value);
    }

    private int _maxRetries = 3;

    /// <summary>失败重试次数，夹紧到 ≥0</summary>
    public int MaxRetries
    {
        get => _maxRetries;
        init => _maxRetries = Math.Max(0, value);
    }

    private int _retryBackoffBaseMs = 500;

    /// <summary>重试退避基数（毫秒），夹紧到 ≥1</summary>
    public int RetryBackoffBaseMs
    {
        get => _retryBackoffBaseMs;
        init => _retryBackoffBaseMs = Math.Max(1, value);
    }

    /// <summary>认证类型</summary>
    public HttpAuthType AuthType { get; init; } = HttpAuthType.None;

    /// <summary>Bearer Token（AuthType 为 BearerToken 时必填）</summary>
    public string? BearerToken { get; init; }

    /// <summary>健康检查路径，如 "/health"；留空则使用默认 "/health"（ADR-020 P3-7 注释对齐实现）</summary>
    public string? HealthPath { get; init; }
}
