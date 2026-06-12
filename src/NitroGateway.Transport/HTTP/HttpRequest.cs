namespace NitroGateway.Transport.HTTP;

/// <summary>HTTP 请求</summary>
public sealed class HttpRequest
{
    /// <summary>请求路径，相对于 BaseUrl，如 "/api/measurements"</summary>
    public required string Path { get; init; }

    /// <summary>HTTP 方法，默认 POST</summary>
    public HttpMethod Method { get; init; } = HttpMethod.Post;

    /// <summary>请求体（JSON）</summary>
    public string? Body { get; init; }

    /// <summary>自定义请求头</summary>
    public Dictionary<string, string> Headers { get; init; } = [];
}
