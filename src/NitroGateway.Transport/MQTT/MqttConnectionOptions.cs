namespace NitroGateway.Transport.MQTT;

/// <summary>MQTT 连接参数。ADR-020 P3-2：数值项在属性层夹紧（非法配置启动即报错），Host/Port 组合校验在 <c>AddNitroMqtt</c> 注册时执行。</summary>
public sealed record MqttConnectionOptions
{
    // ADR-006 P3-5：Host/Port 由 set 改为 init，与其余属性保持一致（构造后不可变，避免运行期漂移）。

    /// <summary>配置节名（appsettings 中为 "MQTT"）</summary>
    public const string SectionName = "MQTT";

    /// <summary>
    /// 连接地址（必填，AddNitroMqtt 注册时校验非空）
    /// </summary>
    public string Host { get; init; } = "";

    /// <summary>端口号，默认 1883（MQTT 标准端口）；注册时校验 1-65535</summary>
    public int Port { get; init; } = 1883;

    /// <summary>客户端 ID。留空则自动生成</summary>
    public string? ClientId { get; init; }

    /// <summary>用户名（可选）</summary>
    public string? Username { get; init; }

    /// <summary>密码（可选）</summary>
    public string? Password { get; init; }

    /// <summary>是否启用 TLS</summary>
    public bool UseTls { get; init; }

    // ADR-006 P3-5：KeepAliveSeconds 夹紧到 [5, 3600]，防止 0/负值导致 MQTTnet 心跳异常。
    private int _keepAliveSeconds = 60;

    /// <summary>心跳间隔（秒），默认 60；夹紧到 [5, 3600]</summary>
    public int KeepAliveSeconds
    {
        get => _keepAliveSeconds;
        init => _keepAliveSeconds = Math.Clamp(value, 5, 3600);
    }

    private int _maxReconnectAttempts = 10;

    /// <summary>最大重连次数，夹紧到 ≥0。0 表示不自动重连</summary>
    public int MaxReconnectAttempts
    {
        get => _maxReconnectAttempts;
        init => _maxReconnectAttempts = Math.Max(0, value);
    }

    private int _reconnectBackoffBaseMs = 1000;

    /// <summary>重连退避基数（毫秒），夹紧到 ≥1。第 N 次重连间隔 = BaseMs × 2^(N-1)</summary>
    public int ReconnectBackoffBaseMs
    {
        get => _reconnectBackoffBaseMs;
        init => _reconnectBackoffBaseMs = Math.Max(1, value);
    }

    private int _reconnectMaxIntervalMs = 30_000;

    /// <summary>最大重连间隔（毫秒），夹紧到 ≥1</summary>
    public int ReconnectMaxIntervalMs
    {
        get => _reconnectMaxIntervalMs;
        init => _reconnectMaxIntervalMs = Math.Max(1, value);
    }
}
