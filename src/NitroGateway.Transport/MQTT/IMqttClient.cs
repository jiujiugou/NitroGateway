using NitroGateway.Shared;

namespace NitroGateway.Transport.MQTT;

/// <summary>
/// MQTT 客户端接口。
/// 基于 MQTTnet 实现，对上暴露异步方法，统一返回 <see cref="OperationResult"/> 而非抛异常。
/// </summary>
public interface IMqttClient
{
    /// <summary>当前连接状态</summary>
    MqttConnectionState State { get; }

    /// <summary>连接状态变更事件</summary>
    event Action<MqttConnectionState>? StateChanged;

    /// <summary>连接到 Broker</summary>
    Task<OperationResult> ConnectAsync(CancellationToken ct = default);

    /// <summary>断开连接</summary>
    Task<OperationResult> DisconnectAsync(CancellationToken ct = default);

    /// <summary>发布消息到指定主题</summary>
    /// <param name="topic">目标主题</param>
    /// <param name="payload">消息体</param>
    /// <param name="qos">服务质量：0=至多一次，1=至少一次，2=恰好一次</param>
    /// <param name="ct">取消令牌</param>
    Task<OperationResult> PublishAsync(string topic, byte[] payload, int qos = 1, CancellationToken ct = default);

    /// <summary>订阅主题</summary>
    /// <param name="topic">订阅的主题（支持通配符 + / #）</param>
    /// <param name="qos">期望的服务质量等级</param>
    /// <param name="ct">取消令牌</param>
    Task<OperationResult> SubscribeAsync(string topic, int qos = 1, CancellationToken ct = default);

    /// <summary>接收到的消息流。调用方用 <c>await foreach</c> 消费，支持背压</summary>
    IAsyncEnumerable<MqttMessage> Messages { get; }
}
