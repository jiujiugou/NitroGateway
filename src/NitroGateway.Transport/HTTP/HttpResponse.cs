namespace NitroGateway.Transport.HTTP;

/// <summary>HTTP 响应</summary>
public sealed class HttpResponse
{
    /// <summary>HTTP 状态码</summary>
    public int StatusCode { get; init; }

    /// <summary>响应体</summary>
    public string? Body { get; init; }

    /// <summary>是否成功（2xx）</summary>
    public bool IsSuccessStatusCode => StatusCode is >= 200 and < 300;
}
