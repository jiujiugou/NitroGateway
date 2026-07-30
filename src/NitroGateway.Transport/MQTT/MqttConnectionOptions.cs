namespace NitroGateway.Transport.MQTT;

/// <summary>MQTT 连接参数</summary>
public sealed record MqttConnectionOptions
{
    /// <summary>
    /// 连接地址
    /// </summary>
    public string Host { get; set; } = "";
    /// <summary>
    /// 端口号
    /// </summary>
    public int Port { get; set; }

    /// <summary>客户端 ID。留空则自动生成</summary>
    public string? ClientId { get; init; }

    /// <summary>用户名（可选）</summary>
    public string? Username { get; init; }

    /// <summary>密码（可选）</summary>
    public string? Password { get; init; }

    /// <summary>是否启用 TLS</summary>
    public bool UseTls { get; init; }

    /// <summary>心跳间隔（秒），默认 60</summary>
    public int KeepAliveSeconds { get; init; } = 60;

    /// <summary>最大重连次数。0 表示不自动重连</summary>
    public int MaxReconnectAttempts { get; init; } = 10;

    /// <summary>重连退避基数（毫秒）。第 N 次重连间隔 = BaseMs × 2^(N-1)</summary>
    public int ReconnectBackoffBaseMs { get; init; } = 1000;

    /// <summary>最大重连间隔（毫秒）</summary>
    public int ReconnectMaxIntervalMs { get; init; } = 30_000;
}
