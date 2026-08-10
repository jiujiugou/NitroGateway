using System.Threading.Channels;
using NitroGateway.Shared;
using NitroGateway.Transport.MQTT;

namespace NitroGateway.UnitTests;

/// <summary>
/// IngestService 测试用 IMqttClient 替身：记录订阅主题、可注入消息流、状态可控。
/// </summary>
public sealed class FakeMqttClient : IMqttClient
{
    private readonly Channel<MqttMessage> _channel = Channel.CreateUnbounded<MqttMessage>();

    /// <summary>当前连接状态（测试可直接设置，默认已连接）</summary>
    public MqttConnectionState State { get; set; } = MqttConnectionState.Connected;

    /// <summary>已成功订阅的主题（按调用顺序）</summary>
    public List<string> SubscribedTopics { get; } = [];

    /// <inheritdoc />
    public event Action<MqttConnectionState>? StateChanged;

    /// <inheritdoc />
    public IAsyncEnumerable<MqttMessage> Messages => _channel.Reader.ReadAllAsync();

    /// <inheritdoc />
    public Task<OperationResult> ConnectAsync(CancellationToken ct = default)
    {
        State = MqttConnectionState.Connected;
        StateChanged?.Invoke(State);
        return Task.FromResult(OperationResult.Success());
    }

    /// <inheritdoc />
    public Task<OperationResult> DisconnectAsync(CancellationToken ct = default)
    {
        State = MqttConnectionState.Disconnected;
        StateChanged?.Invoke(State);
        return Task.FromResult(OperationResult.Success());
    }

    /// <inheritdoc />
    public Task<OperationResult> PublishAsync(string topic, byte[] payload, int qos = 1, CancellationToken ct = default)
        => Task.FromResult(OperationResult.Success());

    /// <inheritdoc />
    public Task<OperationResult> SubscribeAsync(string topic, int qos = 1, CancellationToken ct = default)
    {
        if (State != MqttConnectionState.Connected)
            return Task.FromResult<OperationResult>(OperationalError.Unavailable("MQTT 未连接，无法订阅"));
        SubscribedTopics.Add(topic);
        return Task.FromResult(OperationResult.Success());
    }

    /// <summary>向消息流注入一条消息（模拟 broker 投递）</summary>
    public void Push(string topic, byte[] payload)
        => _channel.Writer.TryWrite(new MqttMessage { Topic = topic, Payload = payload, ReceivedAt = DateTime.UtcNow });
}
