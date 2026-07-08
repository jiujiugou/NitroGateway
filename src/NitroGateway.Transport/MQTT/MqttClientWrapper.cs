using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using NitroGateway.Shared;
using NitroGateway.Telemetry;
using NitroGateway.Telemetry.Tracing;
using MqttNet = MQTTnet;

namespace NitroGateway.Transport.MQTT;

/// <summary>
/// <see cref="IMqttClient"/> 的 MQTTnet 实现。
/// 封装连接生命周期、自动重连、消息路由，所有操作返回 <see cref="OperationResult"/>。
/// </summary>
public sealed class MqttClientWrapper : IMqttClient, IAsyncDisposable
{
    private readonly MqttConnectionOptions _options;
    private readonly ILogger<MqttClientWrapper> _logger;
    private readonly MqttNet.IMqttClient _inner;
    private readonly Channel<MqttMessage> _channel;
    private int _reconnectCount;
    private CancellationTokenSource? _reconnectCts;

    /// <inheritdoc />
    public MqttConnectionState State { get; private set; } = MqttConnectionState.Disconnected;

    /// <inheritdoc />
    public event Action<MqttConnectionState>? StateChanged;

    /// <inheritdoc />
    public IAsyncEnumerable<MqttMessage> Messages => _channel.Reader.ReadAllAsync();

    /// <summary>
    /// 创建 MQTT 客户端实例。
    /// </summary>
    /// <param name="options">连接参数</param>
    /// <param name="logger">日志记录器</param>
    public MqttClientWrapper(MqttConnectionOptions options, ILogger<MqttClientWrapper> logger)
    {
        _options = options;
        _logger = logger;

        var factory = new MqttNet.MqttClientFactory();
        _inner = factory.CreateMqttClient();
        _channel = Channel.CreateBounded<MqttMessage>(new BoundedChannelOptions(10_000)
        {
            FullMode = BoundedChannelFullMode.Wait
        });

        _inner.ApplicationMessageReceivedAsync += OnMessageReceivedAsync;
        _inner.DisconnectedAsync += OnDisconnectedAsync;
    }

    /// <inheritdoc />
    public async Task<OperationResult> ConnectAsync(CancellationToken ct = default)
    {
        if (State == MqttConnectionState.Connected)
            return OperationResult.Success();

        SetState(MqttConnectionState.Connecting);

        try
        {
            var clientId = _options.ClientId
                           ?? $"NitroGateway-{Environment.MachineName}-{Guid.NewGuid():N}";

            var builder = new MqttNet.MqttClientOptionsBuilder()
                .WithClientId(clientId)
                .WithCleanStart()
                .WithKeepAlivePeriod(TimeSpan.FromSeconds(_options.KeepAliveSeconds));

            if (_options.UseTls)
                builder.WithTlsOptions(o => o.WithSslProtocols(System.Security.Authentication.SslProtocols.Tls12));

            if (!string.IsNullOrEmpty(_options.Username))
                builder.WithCredentials(_options.Username, _options.Password);

            if (_options.Broker.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase))
                builder.WithTcpServer(_options.Broker[6..]);
            else
                builder.WithTcpServer(_options.Broker);

            var result = await _inner.ConnectAsync(builder.Build(), ct);

            if (result.ResultCode == MqttNet.MqttClientConnectResultCode.Success)
            {
                SetState(MqttConnectionState.Connected);
                _reconnectCount = 0;
                return OperationResult.Success();
            }

            SetState(MqttConnectionState.Disconnected);
            return OperationalError.General($"MQTT 连接失败: {result.ResultCode} - {result.ReasonString}");
        }
        catch (Exception ex)
        {
            SetState(MqttConnectionState.Disconnected);
            return OperationalError.General($"MQTT 连接异常: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<OperationResult> DisconnectAsync(CancellationToken ct = default)
    {
        CancelReconnect();

        try
        {
            if (_inner.IsConnected)
            {
                var options = new MqttNet.MqttClientDisconnectOptions
                {
                    Reason = MqttNet.MqttClientDisconnectOptionsReason.NormalDisconnection
                };
                await _inner.DisconnectAsync(options, ct);
            }

            SetState(MqttConnectionState.Disconnected);
            return OperationResult.Success();
        }
        catch (Exception ex)
        {
            return OperationalError.General($"MQTT 断开异常: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<OperationResult> PublishAsync(string topic, byte[] payload, int qos = 1, CancellationToken ct = default)
    {
        using var activity = GatewayActivitySource.Source.StartActivity(GatewayActivities.MqttPublish);
        activity?.SetTag(GatewayActivityTags.MqttTopic, topic);

        if (State != MqttConnectionState.Connected)
        {
            activity?.SetStatus(ActivityStatusCode.Error);
            activity?.SetTag(GatewayActivityTags.ErrorMessage, "MQTT 未连接");
            return OperationalError.Unavailable($"MQTT 未连接，无法发布到 {topic}");
        }

        try
        {
            var qosLevel = qos switch
            {
                0 => MQTTnet.Protocol.MqttQualityOfServiceLevel.AtMostOnce,
                1 => MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce,
                2 => MQTTnet.Protocol.MqttQualityOfServiceLevel.ExactlyOnce,
                _ => MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce
            };

            var msg = new MqttNet.MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(payload)
                .WithQualityOfServiceLevel(qosLevel)
                .Build();

            var result = await _inner.PublishAsync(msg, ct);

            if (result.ReasonCode == MqttNet.MqttClientPublishReasonCode.Success)
            {
                activity?.SetStatus(ActivityStatusCode.Ok);
                return OperationResult.Success();
            }

            activity?.SetStatus(ActivityStatusCode.Error);
            activity?.SetTag(GatewayActivityTags.ErrorMessage, $"MQTT 发布失败: {result.ReasonCode}");
            return OperationalError.General($"MQTT 发布失败: {result.ReasonCode} - {result.ReasonString}");
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error);
            activity?.SetTag(GatewayActivityTags.ErrorMessage, ex.ToString());
            return OperationalError.General($"MQTT 发布异常: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<OperationResult> SubscribeAsync(string topic, int qos = 1, CancellationToken ct = default)
    {
        if (State != MqttConnectionState.Connected)
            return OperationalError.Unavailable($"MQTT 未连接，无法订阅 {topic}");

        try
        {
            var qosLevel = qos switch
            {
                0 => MQTTnet.Protocol.MqttQualityOfServiceLevel.AtMostOnce,
                1 => MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce,
                2 => MQTTnet.Protocol.MqttQualityOfServiceLevel.ExactlyOnce,
                _ => MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce
            };

            var options = new MqttNet.MqttClientSubscribeOptionsBuilder()
                .WithTopicFilter(topic, qosLevel)
                .Build();

            var result = await _inner.SubscribeAsync(options, ct);
            var item = result.Items.FirstOrDefault();

            if (item is not null && item.ResultCode is MqttNet.MqttClientSubscribeResultCode.GrantedQoS0
                                       or MqttNet.MqttClientSubscribeResultCode.GrantedQoS1
                                       or MqttNet.MqttClientSubscribeResultCode.GrantedQoS2)
                return OperationResult.Success();

            return OperationalError.General($"MQTT 订阅失败: {item?.ResultCode}");
        }
        catch (Exception ex)
        {
            return OperationalError.General($"MQTT 订阅异常: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        CancelReconnect();

        _inner.ApplicationMessageReceivedAsync -= OnMessageReceivedAsync;
        _inner.DisconnectedAsync -= OnDisconnectedAsync;

        if (_inner.IsConnected)
        {
            var options = new MqttNet.MqttClientDisconnectOptions
            {
                Reason = MqttNet.MqttClientDisconnectOptionsReason.NormalDisconnection
            };
            await _inner.DisconnectAsync(options);
        }

        _inner.Dispose();
        _channel.Writer.Complete();
        _reconnectCts?.Dispose();
    }

    // ---- 内部实现 ----

    /// <summary>更新连接状态并触发 <see cref="StateChanged"/> 事件</summary>
    private void SetState(MqttConnectionState state)
    {
        if (State == state) return;
        var old = State;
        State = state;
        NitroMetrics.MqttState.Set((int)state);
        _logger.LogDebug("MQTT 状态变更: {Old} → {New}", old, state);
        StateChanged?.Invoke(state);
    }

    /// <summary>MQTTnet 消息回调：将 MQTT 消息写入 Channel 管道供外部消费</summary>
    private Task OnMessageReceivedAsync(MqttNet.MqttApplicationMessageReceivedEventArgs e)
    {
        var payload = e.ApplicationMessage.Payload;
        var payloadBytes = new byte[payload.Length];
        var offset = 0;
        foreach (var segment in payload)
        {
            segment.Span.CopyTo(payloadBytes.AsSpan(offset));
            offset += segment.Length;
        }

        var msg = new MqttMessage
        {
            Topic = e.ApplicationMessage.Topic,
            Payload = payloadBytes,
            Qos = (int)e.ApplicationMessage.QualityOfServiceLevel,
            ReceivedAt = DateTime.UtcNow
        };

        if (!_channel.Writer.TryWrite(msg))
            _logger.LogWarning("消息通道已满，丢弃消息: {Topic}", msg.Topic);

        return Task.CompletedTask;
    }

    /// <summary>MQTTnet 断开回调：如配置了自动重连则启动重连流程</summary>
    private async Task OnDisconnectedAsync(MqttNet.MqttClientDisconnectedEventArgs e)
    {
        _logger.LogWarning("MQTT 意外断开: {Reason}", e.Reason);

        // Faulted/Disconnected → 不重连（已经放弃或主动断开）
        if (State is MqttConnectionState.Faulted or MqttConnectionState.Disconnected)
            return;

        if (_options.MaxReconnectAttempts == 0)
        {
            SetState(MqttConnectionState.Disconnected);
            return;
        }

        await TryReconnectAsync();
    }

    /// <summary>指数退避自动重连，超过最大次数后状态变为 Faulted</summary>
    private async Task TryReconnectAsync()
    {
        CancelReconnect();
        _reconnectCts = new CancellationTokenSource();
        var token = _reconnectCts.Token;

        SetState(MqttConnectionState.Reconnecting);

        while (_reconnectCount < _options.MaxReconnectAttempts && !token.IsCancellationRequested)
        {
            _reconnectCount++;

            var delayMs = Math.Min(
                _options.ReconnectBackoffBaseMs * (int)Math.Pow(2, _reconnectCount - 1),
                _options.ReconnectMaxIntervalMs);

            _logger.LogInformation("MQTT 重连 {Attempt}/{Max}，等待 {Delay}ms",
                _reconnectCount, _options.MaxReconnectAttempts, delayMs);

            try { await Task.Delay(delayMs, token); }
            catch (OperationCanceledException) { return; }

            var result = await ConnectAsync(token);
            if (result.IsSuccess) return;
        }

        _logger.LogError("MQTT 重连失败，已达最大重试次数 {Max}", _options.MaxReconnectAttempts);
        SetState(MqttConnectionState.Faulted);
    }

    /// <summary>取消当前进行中的重连尝试</summary>
    private void CancelReconnect()
    {
        _reconnectCts?.Cancel();
        _reconnectCts?.Dispose();
        _reconnectCts = null;
    }
}
