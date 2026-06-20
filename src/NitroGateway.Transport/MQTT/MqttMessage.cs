namespace NitroGateway.Transport.MQTT;

/// <summary>收到的 MQTT 消息</summary>
public sealed record MqttMessage
{
    /// <summary>主题</summary>
    public required string Topic { get; init; }

    /// <summary>消息体</summary>
    public required byte[] Payload { get; init; }

    /// <summary>服务质量等级</summary>
    public int Qos { get; init; }

    /// <summary>网关收到该消息的时间</summary>
    public DateTime ReceivedAt { get; init; }
}
