using NitroGateway.Shared;

namespace NitroGateway.Transport.HTTP;

/// <summary>
/// HTTP 客户端接口。
/// 基于 <c>IHttpClientFactory</c> + Polly 实现，统一返回 <see cref="OperationResult"/>。
/// </summary>
public interface IHttpClient
{
    /// <summary>当前连接状态</summary>
    HttpConnectionState State { get; }

    /// <summary>连接状态变更事件</summary>
    event Action<HttpConnectionState>? StateChanged;

    /// <summary>发送请求并返回响应</summary>
    Task<OperationResult<HttpResponse>> SendAsync(HttpRequest request, CancellationToken ct = default);

    /// <summary>上传批量数据（Forwarder 专用，JSON 序列化后 POST）</summary>
    Task<OperationResult> UploadAsync<T>(string path, T payload, CancellationToken ct = default);

    /// <summary>健康检查</summary>
    Task<OperationResult> HealthCheckAsync(CancellationToken ct = default);
}
