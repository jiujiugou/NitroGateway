using System.Threading.Channels;
using NitroGateway.DeviceManagement;
using NitroGateway.Shared;
using NitroGateway.Transport.MQTT;

namespace NitroGateway.UnitTests;

/// <summary>
/// 命令测试用 IMqttClient 替身（ADR-069）：可注入消息流、记录订阅主题与发布消息。
/// 与 <see cref="FakeMqttClient"/> 不同，本替身记录 PublishAsync 载荷，供回执断言使用。
/// </summary>
public sealed class RecordingFakeMqttClient : IMqttClient
{
    private readonly Channel<MqttMessage> _channel = Channel.CreateUnbounded<MqttMessage>();

    /// <summary>当前连接状态（测试可直接设置，默认已连接）</summary>
    public MqttConnectionState State { get; set; } = MqttConnectionState.Connected;

    /// <summary>已成功订阅的主题（按调用顺序）</summary>
    public List<string> SubscribedTopics { get; } = [];

    /// <summary>已发布的 (topic, payload) 元组（按调用顺序）</summary>
    public List<(string Topic, byte[] Payload)> Published { get; } = [];

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
    {
        Published.Add((topic, payload));
        return Task.FromResult(OperationResult.Success());
    }

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

/// <summary>
/// 命令测试用 IWriteService 替身（ADR-069）：记录写请求，可配置成功/失败/抛异常。
/// </summary>
public sealed class FakeWriteService : IWriteService
{
    /// <summary>收到的写请求（按调用顺序）</summary>
    public List<WriteRequest> Requests { get; } = [];

    /// <summary>可配置处理结果；为 null 时返回成功</summary>
    public Func<WriteRequest, OperationResult>? Handler { get; set; }

    /// <summary>可配置抛出的异常；非 null 时优先于 <see cref="Handler"/></summary>
    public Exception? Throw { get; set; }

    /// <inheritdoc />
    public Task<OperationResult> WriteAsync(WriteRequest request, CancellationToken ct = default)
    {
        Requests.Add(request);
        if (Throw is not null)
            return Task.FromException<OperationResult>(Throw);
        var result = Handler?.Invoke(request) ?? OperationResult.Success();
        return Task.FromResult(result);
    }
}
