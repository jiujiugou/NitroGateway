using MQTTnet;
using NitroGateway.Shared;
using NitroGateway.Storage.Buffer;
using NitroGateway.Transport.MQTT;

namespace NitroGateway.CoyoteTests;

/// <summary>
/// MqttClientWrapper 的 Coyote 测试夹具。
/// 关键：所有会被调度器抢占的方法都 <c>await Task.Yield()</c>——即时完成的 Task 没有交错点（假绿来源）。
/// </summary>
internal sealed class ControllableMqttInner : MQTTnet.IMqttClient
{
    private readonly object _subLock = new();
    private readonly List<string> _subscribedTopics = [];
    private int _connectCalls;
    private int _holders;
    private int _maxHolders;
    private bool _disposed;

    public bool IsConnected { get; private set; }
    public MQTTnet.MqttClientOptions Options { get; private set; } = null!;

    public MQTTnet.MqttClientConnectResultCode ConnectResultCode { get; set; } =
        MQTTnet.MqttClientConnectResultCode.Success;

    public Exception? ConnectException { get; set; }

    /// <summary>非 null 时 ConnectAsync 在让出后等待该门（制造长驻在途连接）。</summary>
    public TaskCompletionSource? ConnectGate { get; set; }

    public int ConnectCalls => Volatile.Read(ref _connectCalls);
    public int MaxConcurrentConnects => Volatile.Read(ref _maxHolders);

    public IReadOnlyList<string> SubscribedTopics
    {
        get { lock (_subLock) return _subscribedTopics.ToArray(); }
    }

    public event Func<MQTTnet.MqttApplicationMessageReceivedEventArgs, Task>? ApplicationMessageReceivedAsync;
    public event Func<MQTTnet.MqttClientConnectedEventArgs, Task>? ConnectedAsync;
    public event Func<MQTTnet.MqttClientConnectingEventArgs, Task>? ConnectingAsync;
    public event Func<MQTTnet.MqttClientDisconnectedEventArgs, Task>? DisconnectedAsync;
    public event Func<MQTTnet.Diagnostics.PacketInspection.InspectMqttPacketEventArgs, Task>? InspectPacketAsync;

    public async Task<MQTTnet.MqttClientConnectResult> ConnectAsync(
        MQTTnet.MqttClientOptions options, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _connectCalls);
        var holders = Interlocked.Increment(ref _holders);
        int observed;
        while (holders > (observed = Volatile.Read(ref _maxHolders)))
        {
            if (Interlocked.CompareExchange(ref _maxHolders, holders, observed) == observed)
                break;
        }

        try
        {
            await Task.Yield();   // 受控交错点
            if (_disposed) throw new ObjectDisposedException(nameof(ControllableMqttInner));
            if (ConnectGate is { } gate) await gate.Task;
            if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
            if (ConnectException is not null) throw ConnectException;

            Options = options;
            if (ConnectResultCode != MQTTnet.MqttClientConnectResultCode.Success)
                return new MQTTnet.MqttClientConnectResult { ResultCode = ConnectResultCode };

            IsConnected = true;
            return new MQTTnet.MqttClientConnectResult
            {
                ResultCode = MQTTnet.MqttClientConnectResultCode.Success,
                IsSessionPresent = false
            };
        }
        finally
        {
            Interlocked.Decrement(ref _holders);
        }
    }

    public Task DisconnectAsync(MQTTnet.MqttClientDisconnectOptions options, CancellationToken cancellationToken)
    {
        IsConnected = false;
        return Task.CompletedTask;
    }

    public Task PingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<MQTTnet.MqttClientPublishResult> PublishAsync(
        MQTTnet.MqttApplicationMessage applicationMessage, CancellationToken cancellationToken)
        => Task.FromResult(new MQTTnet.MqttClientPublishResult(
            null, MQTTnet.MqttClientPublishReasonCode.Success, null, []));

    public Task SendEnhancedAuthenticationExchangeDataAsync(
        MQTTnet.MqttEnhancedAuthenticationExchangeData data, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task<MQTTnet.MqttClientSubscribeResult> SubscribeAsync(
        MQTTnet.MqttClientSubscribeOptions options, CancellationToken cancellationToken)
    {
        lock (_subLock)
        {
            foreach (var filter in options.TopicFilters)
                _subscribedTopics.Add(filter.Topic);
        }

        var items = options.TopicFilters
            .Select(f => new MQTTnet.MqttClientSubscribeResultItem(
                f, MQTTnet.MqttClientSubscribeResultCode.GrantedQoS1))
            .ToList();
        return Task.FromResult(new MQTTnet.MqttClientSubscribeResult(1, items, null, []));
    }

    public Task<MQTTnet.MqttClientUnsubscribeResult> UnsubscribeAsync(
        MQTTnet.MqttClientUnsubscribeOptions options, CancellationToken cancellationToken)
        => Task.FromResult(new MQTTnet.MqttClientUnsubscribeResult(1, [], null, []));

    /// <summary>模拟已连接状态下意外断开（触发 DisconnectedAsync，ClientWasConnected=true）。</summary>
    public void SimulateDrop(string reason = "connection lost")
    {
        IsConnected = false;
        DisconnectedAsync?.Invoke(new MQTTnet.MqttClientDisconnectedEventArgs(
            true,
            new MQTTnet.MqttClientConnectResult { ResultCode = MQTTnet.MqttClientConnectResultCode.Success },
            MQTTnet.MqttClientDisconnectReason.KeepAliveTimeout,
            reason,
            [],
            null));
    }

    public void Dispose() => _disposed = true;

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>转发开关替身：可手动触发 EnabledChanged。</summary>
internal sealed class ToggleFake : IForwardMqttToggle
{
    public bool IsEnabled { get; set; } = true;

    public event Action<bool>? EnabledChanged;

    public Task<OperationResult> SetEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        IsEnabled = enabled;
        EnabledChanged?.Invoke(enabled);
        return Task.FromResult(OperationResult.Success());
    }

    public Task<OperationResult> InitializeAsync(CancellationToken ct = default)
        => Task.FromResult(OperationResult.Success());
}

/// <summary>状态监听者替身：回调内反向读取状态（验证通知不在锁内 → 不重入死锁）。</summary>
internal sealed class ReentrantListenerFake : IMqttStateListener
{
    private int _started;
    private int _completed;

    public Func<MqttConnectionState>? StateProvider { get; set; }

    public int Started => Volatile.Read(ref _started);
    public int Completed => Volatile.Read(ref _completed);

    public async ValueTask OnStateChangedAsync(MqttConnectionState state, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _started);
        await Task.Yield();
        _ = StateProvider?.Invoke();   // 重入：回调内读 MqttClientWrapper.State
        Interlocked.Increment(ref _completed);
    }
}

/// <summary>重连守卫包装探针：记录同时持权峰值（真实守卫应恒 ≤1）。</summary>
internal sealed class CountingGuard : IReconnectGuard
{
    private readonly IReconnectGuard _inner;
    private int _holders;
    private int _max;

    public CountingGuard(IReconnectGuard inner) => _inner = inner;

    public int MaxConcurrentHolders => Volatile.Read(ref _max);

    public bool TryBegin()
    {
        if (!_inner.TryBegin()) return false;
        var current = Interlocked.Increment(ref _holders);
        int observed;
        while (current > (observed = Volatile.Read(ref _max)))
        {
            if (Interlocked.CompareExchange(ref _max, current, observed) == observed)
                break;
        }
        return true;
    }

    public void End()
    {
        Interlocked.Decrement(ref _holders);
        _inner.End();
    }
}

internal static class MqttTestData
{
    public static MqttConnectionOptions Options(int maxAttempts = 1) => new()
    {
        Host = "localhost",
        Port = 1883,
        MaxReconnectAttempts = maxAttempts,
        ReconnectBackoffBaseMs = 1,
        ReconnectMaxIntervalMs = 1
    };
}
