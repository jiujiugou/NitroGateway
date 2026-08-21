using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using NitroGateway.Shared;
using NitroGateway.Storage.Buffer;
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
    private readonly IEnumerable<IMqttStateListener> _stateListeners;
    // ADR-061：转发总开关——关闭时断开连接且停止重连，状态置 Disabled；
    // null 表示未注册开关（如 Ingest 中心宿主），视为恒启用，行为与旧版一致。
    private readonly IForwardMqttToggle? _toggle;

    // ADR-006 P1-2：记录已订阅主题（topic→qos）。CleanStart 会话断开即清订阅，
    // 重连成功后必须重放，否则下行通道静默失效。
    private readonly object _subscriptionLock = new();
    private readonly Dictionary<string, int> _subscriptions = new();

    // ADR-006 P1-3：保证任意时刻只有一个重连循环在跑。
    // ConnectAsync 失败、DisconnectedAsync 事件、MqttHostedService 监督循环都可能触发，这里统一去重。
    private readonly object _reconnectLock = new();
    private bool _reconnectLoopActive;

    // ADR-020 P3-5：State/SetState 用锁同步——Singleton 实例可能被 Forwarder + MqttAlarmNotifier
    // 并发发布/重连路径并发读改，无同步时状态机可能被写丢（读-改-写非原子）。
    private readonly object _stateLock = new();
    private MqttConnectionState _state = MqttConnectionState.Disconnected;

    /// <summary>客户端 ID：构造时固定一次（配置缺失时自动生成），避免每次 ConnectAsync 重新生成导致会话漂移（ADR-020 P3-7）</summary>
    private readonly string _clientId;

    private int _reconnectCount;
    private CancellationTokenSource? _reconnectCts;

    /// <inheritdoc />
    public MqttConnectionState State
    {
        get { lock (_stateLock) return _state; }
    }

    /// <inheritdoc />
    public event Action<MqttConnectionState>? StateChanged;

    /// <inheritdoc />
    public IAsyncEnumerable<MqttMessage> Messages => _channel.Reader.ReadAllAsync();

    /// <summary>
    /// 创建 MQTT 客户端实例。
    /// </summary>
    /// <param name="options">连接参数</param>
    /// <param name="logger">日志记录器</param>
    /// <param name="forwardMqttToggle">转发总开关（ADR-061）；null 视为恒启用</param>
    public MqttClientWrapper(
        MqttConnectionOptions options,
        ILogger<MqttClientWrapper> logger,
        IEnumerable<IMqttStateListener> stateListeners,
        IForwardMqttToggle? forwardMqttToggle = null)
        : this(options, logger, new MqttNet.MqttClientFactory().CreateMqttClient(), stateListeners, forwardMqttToggle)
    {
    }

    /// <summary>
    /// 测试/组合用构造函数：允许注入 MQTTnet 客户端替身与状态监听者
    /// （NitroGateway.IntegrationTests 专用，用于模拟断线/重连/订阅重放/状态推送，无需真实 broker）。
    /// </summary>
    internal MqttClientWrapper(
        MqttConnectionOptions options,
        ILogger<MqttClientWrapper> logger,
        MqttNet.IMqttClient inner,
        IEnumerable<IMqttStateListener> stateListeners,
        IForwardMqttToggle? forwardMqttToggle = null)
    {
        _options = options;
        _logger = logger;
        _inner = inner;
        _stateListeners = stateListeners;
        _toggle = forwardMqttToggle;
        // ADR-061：订阅开关状态变更——关闭即断开并停止重连，开启即恢复连接
        if (_toggle is not null)
            _toggle.EnabledChanged += OnEnabledChanged;
        // ADR-020 P3-7：ClientId 构造时固定（绕过 AddNitroMqtt 直接构造时也只会生成一次），
        // 避免每次 ConnectAsync 生成新 ID 造成 CleanStart 会话漂移。
        _clientId = options.ClientId ?? $"NitroGateway-{Environment.MachineName}-{Guid.NewGuid():N}";
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
        // ADR-061：转发总开关关闭时直接拒绝连接——不置 Connecting、不触发重连，状态保持 Disabled
        if (_toggle is not null && !_toggle.IsEnabled)
        {
            SetState(MqttConnectionState.Disabled);
            return OperationalError.General("MQTT 已关闭（转发开关关闭），不建立连接");
        }

        if (State == MqttConnectionState.Connected)
            return OperationResult.Success();

        SetState(MqttConnectionState.Connecting);

        try
        {
            var builder = new MqttNet.MqttClientOptionsBuilder()
                .WithClientId(_clientId)
                .WithCleanStart()
                .WithKeepAlivePeriod(TimeSpan.FromSeconds(_options.KeepAliveSeconds));

            if (_options.UseTls)
                builder.WithTlsOptions(o => o.WithSslProtocols(System.Security.Authentication.SslProtocols.Tls12));

            if (!string.IsNullOrEmpty(_options.Username))
                builder.WithCredentials(_options.Username, _options.Password);

            builder.WithTcpServer(_options.Host, _options.Port);

            var result = await _inner.ConnectAsync(builder.Build(), ct);

            if (result.ResultCode == MqttNet.MqttClientConnectResultCode.Success)
            {
                // ADR-061 竞态防护：连接成功瞬间开关被关——立即断开并回 Disabled，
                // 避免 UI 短暂显示「已连接」与「已关闭」不一致。
                if (_toggle is not null && !_toggle.IsEnabled)
                {
                    _logger.LogInformation("MQTT 连接成功但转发开关已关闭，立即断开");
                    SetState(MqttConnectionState.Disabled);
                    try
                    {
                        var disconnectOptions = new MqttNet.MqttClientDisconnectOptions
                        {
                            Reason = MqttNet.MqttClientDisconnectOptionsReason.NormalDisconnection
                        };
                        await _inner.DisconnectAsync(disconnectOptions, ct);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "MQTT 关闭开关断开连接异常");
                    }
                    return OperationalError.General("MQTT 已关闭（转发开关关闭）");
                }

                SetState(MqttConnectionState.Connected);
                _reconnectCount = 0;
                // ADR-006 P1-2：CleanStart 会话重连后订阅已丢，这里重放记录过的订阅
                await ReplaySubscriptionsAsync(ct);
                return OperationResult.Success();
            }

            // ADR-006 P1-3：连接被拒绝也纳入重连流程（不依赖 DisconnectedAsync 事件时序）
            return HandleConnectFailure($"MQTT 连接失败: {result.ResultCode} - {result.ReasonString}");
        }
        catch (OperationCanceledException)
        {
            // ADR-020 P1-2：取消不是连接失败——不触发重连（重连循环用独立 CTS，取消后继续重连会破坏停机语义），
            // 回落到 Disconnected 后上抛，交调用方停机/取消路径处理。
            // ADR-061：开关关闭触发的取消（CancelReconnect）不得把 Disabled 覆盖成 Disconnected
            SetState(_toggle is not null && !_toggle.IsEnabled
                ? MqttConnectionState.Disabled
                : MqttConnectionState.Disconnected);
            throw;
        }
        catch (Exception ex)
        {
            return HandleConnectFailure($"MQTT 连接异常: {ex.Message}");
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

            // ADR-061：开关关闭时断开统一回到 Disabled，而非可被监督循环重连的 Disconnected
            SetState(_toggle is not null && !_toggle.IsEnabled
                ? MqttConnectionState.Disabled
                : MqttConnectionState.Disconnected);
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

            if (result.ReasonCode is MqttNet.MqttClientPublishReasonCode.Success or
                MqttNet.MqttClientPublishReasonCode.NoMatchingSubscribers)
            {
                // ADR-020 P3-6：NoMatchingSubscribers 按成功处理——QoS1 为尽力投递，无订阅者时
                // 消息被 Broker 丢弃但没有送达对象，不计失败不重试；遥测场景可接受，注释明确决策。
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
            {
                // ADR-006 P1-2：记录成功订阅，供重连后重放
                lock (_subscriptionLock) _subscriptions[topic] = qos;
                return OperationResult.Success();
            }

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
        if (_toggle is not null)
            _toggle.EnabledChanged -= OnEnabledChanged;
        CancelReconnect();
        // ADR-006 P3-4：关停期间立即置 Disconnected，避免 MqttHealthCheck 短暂仍报 Healthy
        SetState(MqttConnectionState.Disconnected);

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
        MqttConnectionState old;
        lock (_stateLock)
        {
            old = _state;
            if (old == state) return;
            _state = state;
        }
        NitroMetrics.MqttState.Set((int)state);
        _logger.LogDebug("MQTT 状态变更: {Old} → {New}", old, state);

        // 事件与监听者通知在锁外执行，避免监听者回调（可能反向调用 State）造成重入死锁
        StateChanged?.Invoke(state);
        NotifyStateListeners(state);
    }

    /// <summary>
    /// ADR-061：订阅转发总开关状态变更——关闭即断开并停止重连，开启即恢复连接。
    /// fire-and-forget 启动，异常已在下游方法内部隔离，不抛回事件源线程（Controller/UI）。
    /// </summary>
    private void OnEnabledChanged(bool enabled)
    {
        if (enabled)
            _ = ApplyEnabledAsync();
        else
            _ = ApplyDisabledAsync();
    }

    /// <summary>
    /// ADR-061：开关关闭——取消重连 + 置 Disabled + 断开内层连接。
    /// <para><b>顺序关键：</b>先 SetState(Disabled) 再断开，避免 <see cref="OnDisconnectedAsync"/>
    /// 在状态仍为 Connected 时误启动重连循环（否则关闭会被重连撤销）。</para>
    /// </summary>
    private async Task ApplyDisabledAsync(CancellationToken ct = default)
    {
        CancelReconnect();
        SetState(MqttConnectionState.Disabled);
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
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 正常取消（停机），忽略
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MQTT 关闭开关断开连接异常");
        }
    }

    /// <summary>
    /// ADR-061：开关开启——恢复连接（订阅重放由 CleanStart + <see cref="ReplaySubscriptionsAsync"/> 兜底）。
    /// </summary>
    private async Task ApplyEnabledAsync(CancellationToken ct = default)
    {
        try
        {
            if (_toggle is not null && !_toggle.IsEnabled)
                return; // 开启后又被关回，交给下一次事件处理
            if (State == MqttConnectionState.Connected)
                return;

            var r = await ConnectAsync(ct);
            if (r.IsFailure)
                _logger.LogWarning("MQTT 开关开启后连接失败: {Error}", r.Error?.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MQTT 开关开启后连接异常");
        }
    }

    /// <summary>
    /// ADR-020 P1-1：通知注册的 <see cref="IMqttStateListener"/>（SignalR 推送等）。
    /// fire-and-forget + 异常隔离——监听者故障只记日志，不影响连接状态机与事件链。
    /// </summary>
    private void NotifyStateListeners(MqttConnectionState state)
    {
        foreach (var listener in _stateListeners)
        {
            _ = NotifyListenerAsync(listener, state);
        }
    }

    private async Task NotifyListenerAsync(IMqttStateListener listener, MqttConnectionState state)
    {
        try
        {
            await listener.OnStateChangedAsync(state);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MQTT 状态监听者通知失败: {Listener}", listener.GetType().Name);
        }
    }

    /// <summary>
    /// MQTTnet 消息回调：将 MQTT 消息写入 Channel 管道供外部消费。
    /// ADR-006 P3-1：当前无下行订阅（云端指令走 HTTP，见 Transport/DESIGN.md），通道保留给未来消费者；
    /// 若未来落地命令下行，命令类消息应改用 WriteAsync 阻塞写入（或独立小容量队列）避免静默丢失。
    /// </summary>
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
            ReceivedAt = DateTime.UtcNow,
            // ADR-036：携带发送方 ClientId（含机器名），供中心站点注册/冲突检测
            ClientId = _clientId
        };

        if (!_channel.Writer.TryWrite(msg))
            _logger.LogWarning("消息通道已满，丢弃消息: {Topic}", msg.Topic);

        return Task.CompletedTask;
    }

    /// <summary>
    /// MQTTnet 断开回调。
    /// ADR-006 P1-3：只有"已连接后意外断开"才在这里启动重连；
    /// 首连失败由 ConnectAsync 的 HandleConnectFailure 兜底（不依赖事件时序），
    /// Disconnected/Faulted（已放弃或主动断开）、Reconnecting（循环已运行）直接忽略。
    /// </summary>
    private Task OnDisconnectedAsync(MqttNet.MqttClientDisconnectedEventArgs e)
    {
        if (e.ClientWasConnected)
            _logger.LogWarning("MQTT 意外断开: {Reason}", e.Reason);
        else
            _logger.LogDebug("MQTT 连接失败: {Reason}", e.Reason);

        if (_options.MaxReconnectAttempts == 0)
        {
            SetState(MqttConnectionState.Disconnected);
            return Task.CompletedTask;
        }

        if (State == MqttConnectionState.Connected)
            StartReconnectLoop();

        return Task.CompletedTask;
    }

    /// <summary>
    /// 指数退避自动重连。超过最大次数后状态变为 Faulted，
    /// 由 MqttHostedService 监督循环周期复位（ADR-006 P1-3）。
    /// </summary>
    private async Task TryReconnectAsync()
    {
        try
        {
            try
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

                    try
                    {
                        var result = await ConnectAsync(token);
                        if (result.IsSuccess) return;
                    }
                    catch (OperationCanceledException)
                    {
                        // ADR-020 P1-2：取消（DisconnectAsync/DisposeAsync 触发 CancelReconnect）正常退出重连
                        return;
                    }
                }

                _logger.LogError("MQTT 重连失败，已达最大重试次数 {Max}", _options.MaxReconnectAttempts);
                SetState(MqttConnectionState.Faulted);
            }
            finally
            {
                // ADR-006 P3-2：成功/失败/取消退出循环都释放 CTS，避免残留到下次断开才清理
                _reconnectCts?.Dispose();
                _reconnectCts = null;
                lock (_reconnectLock) _reconnectLoopActive = false;
            }
        }
        catch (Exception ex)
        {
            // ADR-020 P3-3：fire-and-forget 启动的重连循环必须兜底任何未预期异常——否则未观测异常
            // 且状态卡在 Reconnecting 永不自愈；置 Faulted 交由 MqttHostedService 监督循环周期复位。
            _logger.LogError(ex, "MQTT 重连循环异常，置 Faulted 由监督循环兜底");
            SetState(MqttConnectionState.Faulted);
        }
    }

    /// <summary>启动重连循环（单实例，已运行则跳过），供 ConnectAsync 失败与断开事件共用</summary>
    private void StartReconnectLoop()
    {
        lock (_reconnectLock)
        {
            if (_reconnectLoopActive) return;
            _reconnectLoopActive = true;
        }
        _ = TryReconnectAsync();
    }

    /// <summary>
    /// ADR-006 P1-3：连接失败统一处理——配置了自动重连则确定性启动重连循环
    /// （内部保证单实例，循环自身调用不会重复触发），否则回落到 Disconnected。
    /// </summary>
    private OperationResult HandleConnectFailure(string message)
    {
        if (_options.MaxReconnectAttempts > 0)
            StartReconnectLoop();
        else
            SetState(MqttConnectionState.Disconnected);
        return OperationalError.General(message);
    }

    /// <summary>ADR-006 P1-2：重放已订阅主题；订阅失败仅记警告，保留记录供下次重连继续重放</summary>
    private async Task ReplaySubscriptionsAsync(CancellationToken ct)
    {
        KeyValuePair<string, int>[] subscriptions;
        lock (_subscriptionLock) subscriptions = _subscriptions.ToArray();

        foreach (var subscription in subscriptions)
        {
            var r = await SubscribeAsync(subscription.Key, subscription.Value, ct);
            if (r.IsFailure)
                _logger.LogWarning("MQTT 重连后重订阅失败: {Topic} - {Error}", subscription.Key, r.Error?.Message);
        }
    }

    /// <summary>取消当前进行中的重连尝试</summary>
    private void CancelReconnect()
    {
        _reconnectCts?.Cancel();
        _reconnectCts?.Dispose();
        _reconnectCts = null;
    }
}
