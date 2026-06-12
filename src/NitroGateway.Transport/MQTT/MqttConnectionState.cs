namespace NitroGateway.Transport.MQTT;

/// <summary>MQTT 连接状态</summary>
public enum MqttConnectionState
{
    /// <summary>未连接</summary>
    Disconnected,

    /// <summary>正在连接</summary>
    Connecting,

    /// <summary>已连接</summary>
    Connected,

    /// <summary>正在重连</summary>
    Reconnecting,

    /// <summary>故障（超过最大重试次数），需外部介入</summary>
    Faulted
}
