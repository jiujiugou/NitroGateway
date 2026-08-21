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

    /// <summary>
    /// 已关闭（ADR-061）：转发总开关关闭时置位——不连接、不重试，等待开关开启后由连接层恢复。
    /// 区别于 <see cref="Faulted"/>（重连超限）与 <see cref="Disconnected"/>（可被监督循环重连）。
    /// </summary>
    Disabled,

    /// <summary>故障（超过最大重试次数），需外部介入</summary>
    Faulted
}
