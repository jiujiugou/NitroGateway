namespace NitroGateway.Transport.HTTP;

/// <summary>HTTP 连接状态</summary>
public enum HttpConnectionState
{
    /// <summary>尚未验证连通性</summary>
    Disconnected,

    /// <summary>连通正常</summary>
    Connected,

    /// <summary>故障（连续失败或熔断打开），需外部介入</summary>
    Faulted
}
