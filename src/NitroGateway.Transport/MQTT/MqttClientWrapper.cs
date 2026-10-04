using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using NitroGateway.Primitives.Resilience;
using NitroGateway.Shared;
using NitroGateway.Storage.Buffer;
using NitroGateway.Telemetry;
using NitroGateway.Telemetry.Tracing;
using Polly;
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
    // null 表示未注册开关（如 Ingest 中心宿主），视为恒启用，行为与旧版一致。
    private readonly IForwardMqttToggle? _toggle;

    // ADR-006 P1-2：记录已订阅主题（topic→qos）。CleanStart 会话断开即清订阅，
    // 重连成功后必须重放，否则下行通道静默失效。
    private readonly object _subscriptionLock = new();
    private readonly Dictionary<string, int> _subscriptions = new();

    // ADR-006 P1-3：保证任意时刻只有一个重连循环在跑。
    // ConnectAsync 失败、DisconnectedAsync 事件、MqttHostedService 监督循环都可能触发，这里统一去重。
    // 守卫抽成可注入接缝（IReconnectGuard），便于 Coyote 系统化验证单实例不变量。
    private readonly IReconnectGuard _reconnectGuard;

    // 并发发布/重连路径并发读改，无同步时状态机可能被写丢（读-改-写非原子）。
    private readonly object _stateLock = new();
    private MqttConnectionState _state = MqttConnectionState.Disconnected;

    private readonly string _clientId;

    // ADR-006 P1-3：重连的重试/退避策略外包给 Polly（指数退避 + 抖动 + 上限），
    // 不再自写 attempt 循环与退避算法（原 int 溢出 bug 由 Polly 的 MaxDelay 结构性消除）。
    private readonly ResiliencePipeline _reconnectPipeline;
    private CancellationTokenSource? _reconnectCts;

    /// <summary>重连 CTS 工厂（测试缝：便于观测 Dispose 次数）。默认创建真实 CTS。</summary>
    private readonly Func<CancellationTokenSource> _reconnectCtsFactory;

    /// <inheritdoc />
    public MqttConnectionState State
    {
        get { lock (_stateLock) return _state; }
    }

    /// <inheritdoc />
    public event Action<MqttConnectionState>? StateChanged;

    /// <inheritdoc />
    public IAsyncEnumerable<MqttMessage> Messages => _channel.Reader.ReadAllAsync();

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
        IForwardMqttToggle? forwardMqttToggle = null,
        IReconnectGuard? reconnectGuard = null,
        Func<CancellationTokenSource>? reconnectCtsFactory = null)
    {
        _options = options;
        _logger = logger;
        _inner = inner;
        _stateListeners = stateListeners;
        _toggle = forwardMqttToggle;
        _reconnectGuard = reconnectGuard ?? new ReconnectGuard();
        _reconnectCtsFactory = reconnectCtsFactory ?? (static () => new CancellationTokenSource());
        if (_toggle is not null)
            _toggle.EnabledChanged += OnEnabledChanged;
        // 避免每次 ConnectAsync 生成新 ID 造成 CleanStart 会话漂移。
        _clientId = options.ClientId ?? $"NitroGateway-{Environment.MachineName}-{Guid.NewGuid():N}";
        _reconnectPipeline = BuildReconnectPipeline(_options, _logger);
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
        if (_toggle is not null && !_toggle.IsEnabled)
        {
            SetState(MqttConnectionState.Disabled);
            return OperationalError.General("MQTT 已关闭（转发开关关闭），不建立连接");
        }

        if (State == MqttConnectionState.Connected)
            return OperationResult.Success();

        SetState(MqttConnectionState.Connecting);

        OperationResult result;
        try
        {
            result = await ConnectCoreAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // 回落到 Disconnected 后上抛，交调用方停机/取消路径处理。
            SetState(_toggle is not null && !_toggle.IsEnabled
                ? MqttConnectionState.Disabled
                : MqttConnectionState.Disconnected);
            throw;
        }
        catch (Exception ex)
        {
            result = OperationalError.General($"MQTT 连接异常: {ex.Message}");
        }

        if (result.IsSuccess)
        {
            SetState(MqttConnectionState.Connected);
            // ADR-006 P1-2：CleanStart 会话重连后订阅已丢，这里重放记录过的订阅
            await ReplaySubscriptionsAsync(ct);
            return OperationResult.Success();
        }

        // 连接过程中转发开关被关闭：不触发重连循环，保持 Disabled。
        if (State == MqttConnectionState.Disabled)
            return result;

        // ADR-006 P1-3：连接被拒绝也纳入重连流程（不依赖 DisconnectedAsync 事件时序）
        return HandleConnectFailure(result.Error?.Message ?? "MQTT 连接失败");
    }

    /// <summary>
    /// 单次连接尝试（不含自动重连触发）：构造选项、调用 MQTTnet、处理"连上后发现开关已关"。
    /// 供 <see cref="ConnectAsync"/> 与 Polly 重连管线共用——这样重连管线不会经
    /// <see cref="HandleConnectFailure"/> 再次触发重连（避免自递归）。
    /// </summary>
    private async Task<OperationResult> ConnectCoreAsync(CancellationToken ct)
    {
        var builder = new MqttNet.MqttClientOptionsBuilder()
            .WithClientId(_clientId)
            .WithCleanStart()
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(_options.KeepAliveSeconds));

        if (_options.UseTls)
            builder.WithTlsOptions(o => o.WithSslProtocols(System.Security.Authentication.SslProtocols.Tls12));

        if (!string.IsNullOrEmpty(_options.Username))
            builder.WithCredentials(_options.Username, _options.Password);

        // 先 WithTcpServer(host,port) 设端点，再用 Action 重载替换 TCP 选项以设置 socket 缓冲：
        // MQTTnet 默认 BufferSize 仅 8KB，高 RTT 公网链路上会把单条发布卡到 ~1 个 RTT（并发无效）；
        // 放大到 MB 级后并发发布才真正提升吞吐。Action 重载会新建 MqttClientTcpOptions，
        // 但 Build() 会用已保存的端点回填 RemoteEndpoint。
        builder.WithTcpServer(_options.Host, _options.Port)
            .WithTcpServer(o => o.BufferSize = _options.BufferSize);

        var result = await _inner.ConnectAsync(builder.Build(), ct);

        if (result.ResultCode != MqttNet.MqttClientConnectResultCode.Success)
            return OperationalError.General($"MQTT 连接失败: {result.ResultCode} - {result.ReasonString}");

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

        return OperationResult.Success();
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

            var publishSw = Stopwatch.StartNew();
            MqttNet.MqttClientPublishResult result;
            try
            {
                // WaitAsync(ct)：即使 MQTTnet 内部不观察取消，也能在关停取消时立即退出等待，
                // 否则一次真发 1000 批会把宿主关停拖到分钟级。
                result = await _inner.PublishAsync(msg, ct).WaitAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                activity?.SetStatus(ActivityStatusCode.Error, "发布被取消（关停）");
                throw; // 向上传播，让转发轮快速结束
            }
            publishSw.Stop();
            NitroMetrics.MqttPublishDurationMs.Observe(publishSw.Elapsed.TotalMilliseconds);

            if (result.ReasonCode is MqttNet.MqttClientPublishReasonCode.Success or
                MqttNet.MqttClientPublishReasonCode.NoMatchingSubscribers)
            {
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
            // 断开必须有超时：远程/半死链路下 DisconnectAsync 可能长时间不返回，拖死宿主释放。
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _inner.DisconnectAsync(options, cts.Token);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("MQTT 断开超时，强制释放客户端");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MQTT 断开异常，强制释放客户端");
            }
        }

        _inner.Dispose();
        _channel.Writer.Complete();
        // 防御：CancelReconnect 之后若有迟到重连循环又创建了 CTS，原子取走并释放，避免泄漏
        var leftover = TakeReconnectCts();
        if (leftover is not null)
            DisposeCts(leftover);
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

    private void OnEnabledChanged(bool enabled)
    {
        if (enabled)
            _ = ApplyEnabledAsync();
        else
            _ = ApplyDisabledAsync();
    }

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
    /// 重连管线：重试/退避策略由 Polly 提供（指数退避 + 抖动 + 上限），
    /// 不再自写 attempt 循环与退避算法。第 N 次重连延迟 = Base × 2^(N-1)，封顶 MaxInterval。
    /// <para><b>尝试次数语义：</b>总尝试次数 = <see cref="MqttConnectionOptions.MaxReconnectAttempts"/>
    /// （初始 1 次 + 重试 Max-1 次），与原 ADR-006 P1-3 语义一致。</para>
    /// <para><paramref name="onRetryDelay"/> 仅测试用，用于观测每次重试的实际延迟以验证封顶。</para>
    /// </summary>
    internal static ResiliencePipeline BuildReconnectPipeline(
        MqttConnectionOptions options,
        ILogger? logger = null,
        Action<TimeSpan>? onRetryDelay = null)
        // 机制（Polly 管线构造）由通用工厂统一；此处只给策略参数。
        => ResiliencePipelineFactory.Build(
            new ResiliencePolicy
            {
                // 总尝试次数 = MaxReconnectAttempts（初始 1 + 重试 Max-1）
                MaxRetryAttempts = Math.Max(0, options.MaxReconnectAttempts - 1),
                RetryDelay = TimeSpan.FromMilliseconds(options.ReconnectBackoffBaseMs),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                MaxDelay = TimeSpan.FromMilliseconds(options.ReconnectMaxIntervalMs),
                RetryLogLevel = LogLevel.Information,
                OperationName = "MQTT 重连"
            },
            logger,
            onRetryDelay);

    /// <summary>重连的每次尝试：连不上则抛 <see cref="MqttConnectAttemptException"/> 交给 Polly 决策是否重试。</summary>
    private async ValueTask ExecuteReconnectAttemptAsync(CancellationToken ct)
    {
        var result = await ConnectCoreAsync(ct);
        if (result.IsFailure)
            throw new MqttConnectAttemptException(result.Error?.Message ?? "MQTT 连接失败");

        // 成功：置 Connected 并重放订阅（ADR-006 P1-2）
        SetState(MqttConnectionState.Connected);
        await ReplaySubscriptionsAsync(ct);
    }

    /// <summary>
    /// 自动重连（单实例，由 <see cref="StartReconnectLoop"/> 保证）。
    /// 重试/退避交给 <see cref="_reconnectPipeline"/>；耗尽后置 Faulted，
    /// 由 MqttHostedService 监督循环周期复位（ADR-006 P1-3）。
    /// </summary>
    private async Task TryReconnectAsync()
    {
        try
        {
            // 清理上一轮可能残留（原子取走，避免与外部取消方重复释放）
            CancelReconnect();
            var cts = _reconnectCtsFactory();
            // 先取 token 再暴露字段：外部并发 CancelReconnect 取走并释放后，本线程仍持有有效 token
            var token = cts.Token;
            _reconnectCts = cts;

            SetState(MqttConnectionState.Reconnecting);

            try
            {
                // Polly 默认不对 OperationCanceledException 重试；取消（关停/开关关闭）经 token 传播后
                // 直接抛出，状态由取消方设置，这里无需再改状态。
                await _reconnectPipeline.ExecuteAsync(ExecuteReconnectAttemptAsync, token);
            }
            catch (OperationCanceledException)
            {
                // 已取消：状态由 CancelReconnect 的调用方（Disconnect/Disable/Dispose）设置
            }
            catch (MqttConnectAttemptException ex)
            {
                _logger.LogError("MQTT 重连失败，已达最大重试次数 {Max}: {Error}",
                    _options.MaxReconnectAttempts, ex.Message);
                SetState(MqttConnectionState.Faulted);
            }
        }
        catch (Exception ex)
        {
            // 兜底：避免状态卡在 Reconnecting 永不自愈；置 Faulted 交由监督循环周期复位。
            _logger.LogError(ex, "MQTT 重连循环异常，置 Faulted 由监督循环兜底");
            SetState(MqttConnectionState.Faulted);
        }
        finally
        {
            // ADR-006 P3-2：原子取走（外部可能已取消并取走）→ 恰释放一次，避免重复释放/漏释放。
            // 外部取消时由 CancelReconnect 负责 Cancel+Dispose；此处仅负责正常退出路径的释放。
            var leftover = TakeReconnectCts();
            if (leftover is not null)
                DisposeCts(leftover);
            _reconnectGuard.End();
        }
    }

    /// <summary>重连单次尝试失败信号：抛出以驱动 Polly 重试；耗尽后由 <see cref="TryReconnectAsync"/> 置 Faulted。</summary>
    private sealed class MqttConnectAttemptException(string message) : Exception(message);

    /// <summary>启动重连循环（单实例，已运行则跳过），供 ConnectAsync 失败与断开事件共用</summary>
    private void StartReconnectLoop()
    {
        if (!_reconnectGuard.TryBegin()) return;
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

    /// <summary>
    /// 取消并释放当前进行中的重连 CTS（原子取走 → Cancel → Dispose 恰一次）。
    /// <para><b>并发契约：</b><c>Interlocked.Exchange</c> 保证重连循环 <c>finally</c> 与外部取消方
    /// （Disconnect/Disable/Dispose）之间恰一方取得非空实例——消除重复释放、对已释放实例
    /// <c>Cancel()</c> 抛 <see cref="ObjectDisposedException"/>、以及取消后漏释放三类竞态。</para>
    /// </summary>
    private void CancelReconnect()
    {
        var cts = TakeReconnectCts();
        if (cts is null)
            return;
        try { cts.Cancel(); }
        catch (ObjectDisposedException) { /* 已被释放：幂等忽略 */ }
        catch (Exception ex) { _logger.LogDebug(ex, "取消重连 CTS 异常"); }
        DisposeCts(cts);
    }

    /// <summary>原子取走当前重连 CTS（唯一所有权语义）；无则返回 null。</summary>
    private CancellationTokenSource? TakeReconnectCts() => Interlocked.Exchange(ref _reconnectCts, null);

    private void DisposeCts(CancellationTokenSource cts)
    {
        try { cts.Dispose(); }
        catch (Exception ex) { _logger.LogDebug(ex, "释放重连 CTS 异常"); }
    }
}
