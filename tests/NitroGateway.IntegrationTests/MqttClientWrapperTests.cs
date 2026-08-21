using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NitroGateway.Shared;
using NitroGateway.Storage.Buffer;
using NitroGateway.Transport.MQTT;
using Xunit;

namespace NitroGateway.IntegrationTests;

/// <summary>
/// ADR-006 Transport MQTT 修复测试：
/// P1-1 ClientId 唯一性、P1-2 重连重放订阅、P1-3 首连失败/耗尽后恢复、P3-2 CTS 释放、P3-4 Dispose 状态、P3-5 选项边界。
/// </summary>
public class MqttClientWrapperTests
{
    private static MqttConnectionOptions FastReconnectOptions(int maxAttempts = 10) => new()
    {
        Host = "localhost",
        Port = 1883,
        MaxReconnectAttempts = maxAttempts,
        ReconnectBackoffBaseMs = 30,
        ReconnectMaxIntervalMs = 60
    };

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("条件未在超时内满足");
            await Task.Delay(20);
        }
    }

    private sealed class RecordingStateListener : IMqttStateListener
    {
        public List<MqttConnectionState> States { get; } = [];

        public ValueTask OnStateChangedAsync(MqttConnectionState state, CancellationToken ct = default)
        {
            States.Add(state);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>ADR-061 测试替身：转发总开关（内存态，SetEnabled 触发事件）。</summary>
    private sealed class ToggleFake : IForwardMqttToggle
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

    [Fact]
    public void AddNitroMqtt_AutoClientId_IsUniqueAndPrefixed()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MQTT:Host"] = "localhost",
                ["MQTT:Port"] = "1883"
            })
            .Build();

        var sp1 = new ServiceCollection().AddNitroMqtt(config).BuildServiceProvider();
        var sp2 = new ServiceCollection().AddNitroMqtt(config).BuildServiceProvider();
        using (sp1)
        using (sp2)
        {
            var o1 = sp1.GetRequiredService<MqttConnectionOptions>();
            var o2 = sp2.GetRequiredService<MqttConnectionOptions>();

            // ADR-006 P1-1：修复前整串取 [..8] 恒为 "NitroGat"，此处必须两实例不同
            Assert.NotNull(o1.ClientId);
            Assert.NotNull(o2.ClientId);
            Assert.NotEqual(o1.ClientId, o2.ClientId);
            Assert.StartsWith($"NitroGateway-{Environment.MachineName}-", o1.ClientId);
            // P3-5：Host/Port 改为 init 后，ConfigurationBinder 必须仍能正确绑定
            Assert.Equal("localhost", o1.Host);
            Assert.Equal(1883, o1.Port);
        }
    }

    [Fact]
    public async Task AddNitroMqtt_WithoutToggle_ResolvesAsAlwaysEnabled()
    {
        // ADR-061：未注册 IForwardMqttToggle 的宿主（如 Ingest 中心）也能解析 IMqttClient，
        // 且视为恒启用（GetService 返回 null → wrapper 内部跳过开关检查，行为与旧版一致）。
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MQTT:Host"] = "localhost",
                ["MQTT:Port"] = "1883"
            })
            .Build();

        await using var sp = new ServiceCollection()
            .AddLogging()
            .AddNitroMqtt(config)
            .BuildServiceProvider();
        var client = sp.GetRequiredService<IMqttClient>();

        Assert.NotNull(client);
        Assert.Equal(MqttConnectionState.Disconnected, client.State);
        // 服务提供者关闭时会同步 DisposeAsync 单例客户端，此处不显式释放避免双重释放
    }

    [Fact]
    public void KeepAliveSeconds_IsClampedTo5_3600()
    {
        // ADR-006 P3-5：0/负值会破坏 MQTTnet 心跳，夹紧到 [5, 3600]
        Assert.Equal(5, new MqttConnectionOptions { KeepAliveSeconds = 0 }.KeepAliveSeconds);
        Assert.Equal(5, new MqttConnectionOptions { KeepAliveSeconds = -1 }.KeepAliveSeconds);
        Assert.Equal(3600, new MqttConnectionOptions { KeepAliveSeconds = 99999 }.KeepAliveSeconds);
        Assert.Equal(60, new MqttConnectionOptions().KeepAliveSeconds);
    }

    [Fact]
    public void HostPort_AreImmutable()
    {
        // ADR-006 P3-5：Host/Port 与其余属性一致为 init，with 表达式生成新实例而非就地修改
        var a = new MqttConnectionOptions { Host = "h1", Port = 1883 };
        var b = a with { Port = 1884 };
        Assert.Equal(1883, a.Port);
        Assert.Equal("h1", a.Host);
        Assert.Equal(1884, b.Port);
        Assert.NotSame(a, b);
    }

    [Fact]
    public async Task Reconnect_ReplaysSubscriptions()
    {
        // ADR-006 P1-2：CleanStart 会话断开即清订阅，重连成功后必须重放
        var inner = new FakeMqttInnerClient();
        await using var wrapper = new MqttClientWrapper(FastReconnectOptions(), NullLogger<MqttClientWrapper>.Instance, inner, []);

        Assert.True((await wrapper.ConnectAsync()).IsSuccess);
        Assert.True((await wrapper.SubscribeAsync("nitrogateway/dev1/cmd", 1)).IsSuccess);
        Assert.Single(inner.SubscribedTopics);

        inner.SimulateDrop();

        await WaitUntilAsync(() => inner.SubscribedTopics.Count >= 2, TimeSpan.FromSeconds(5));
        Assert.Equal("nitrogateway/dev1/cmd", inner.SubscribedTopics[1]);
        Assert.Equal(MqttConnectionState.Connected, wrapper.State);
    }

    [Fact]
    public async Task InitialConnectFailure_StartsReconnect_AndRecovers()
    {
        // ADR-006 P1-3：首连失败确定性进入重连（不依赖 DisconnectedAsync 事件时序），broker 恢复后自动连上
        var inner = new FakeMqttInnerClient { ConnectException = new TimeoutException("broker down") };
        await using var wrapper = new MqttClientWrapper(FastReconnectOptions(), NullLogger<MqttClientWrapper>.Instance, inner, []);

        Assert.True((await wrapper.ConnectAsync()).IsFailure);

        await WaitUntilAsync(() => inner.ConnectCalls >= 2, TimeSpan.FromSeconds(5));

        inner.ConnectException = null;
        await WaitUntilAsync(() => wrapper.State == MqttConnectionState.Connected, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ReconnectExhausted_SetsFaulted_ThenCanRecover()
    {
        // ADR-006 P1-3：超过最大重试次数进入 Faulted；监督循环/人工复位后再次 ConnectAsync 可恢复
        var inner = new FakeMqttInnerClient { ConnectResultCode = MQTTnet.MqttClientConnectResultCode.ServerUnavailable };
        await using var wrapper = new MqttClientWrapper(FastReconnectOptions(maxAttempts: 2), NullLogger<MqttClientWrapper>.Instance, inner, []);

        Assert.True((await wrapper.ConnectAsync()).IsFailure);
        await WaitUntilAsync(() => wrapper.State == MqttConnectionState.Faulted, TimeSpan.FromSeconds(5));

        inner.ConnectResultCode = MQTTnet.MqttClientConnectResultCode.Success;
        Assert.True((await wrapper.ConnectAsync()).IsSuccess);
        Assert.Equal(MqttConnectionState.Connected, wrapper.State);
    }

    [Fact]
    public async Task DisposeAsync_SetsDisconnected()
    {
        // ADR-006 P3-4：关停期间健康检查不应短暂仍报 Healthy
        var inner = new FakeMqttInnerClient();
        var wrapper = new MqttClientWrapper(FastReconnectOptions(), NullLogger<MqttClientWrapper>.Instance, inner, []);

        Assert.True((await wrapper.ConnectAsync()).IsSuccess);
        Assert.Equal(MqttConnectionState.Connected, wrapper.State);

        await wrapper.DisposeAsync();
        Assert.Equal(MqttConnectionState.Disconnected, wrapper.State);
    }

    [Fact]
    public async Task SuccessfulReconnect_DisposesReconnectCts()
    {
        // ADR-006 P3-2：重连成功路径释放 CTS；随后主动断开不应因残留 CTS 抛异常
        var inner = new FakeMqttInnerClient();
        await using var wrapper = new MqttClientWrapper(FastReconnectOptions(), NullLogger<MqttClientWrapper>.Instance, inner, []);

        Assert.True((await wrapper.ConnectAsync()).IsSuccess);
        inner.SimulateDrop();
        await WaitUntilAsync(() => wrapper.State == MqttConnectionState.Connected, TimeSpan.FromSeconds(5));

        var disconnect = await wrapper.DisconnectAsync();
        Assert.True(disconnect.IsSuccess);
        Assert.Equal(MqttConnectionState.Disconnected, wrapper.State);
    }

    [Fact]
    public async Task StateChange_NotifiesMqttStateListeners()
    {
        // ADR-020 P1-1：MqttClientWrapper 必须在每次状态变更时通知注册的 IMqttStateListener
        // （SignalR MqttStateChanged 推送依赖此接线，修复前从不调用监听者）
        var inner = new FakeMqttInnerClient();
        var listener = new RecordingStateListener();
        await using var wrapper = new MqttClientWrapper(
            FastReconnectOptions(), NullLogger<MqttClientWrapper>.Instance, inner, [listener]);

        Assert.True((await wrapper.ConnectAsync()).IsSuccess);
        Assert.True((await wrapper.DisconnectAsync()).IsSuccess);

        // 同步完成的监听者：fire-and-forget 但调用与记录在 ConnectAsync 返回前完成，顺序确定
        Assert.Equal(
            new[] { MqttConnectionState.Connecting, MqttConnectionState.Connected, MqttConnectionState.Disconnected },
            listener.States);
    }

    [Fact]
    public async Task ConnectAsync_Cancelled_DoesNotStartReconnectLoop()
    {
        // ADR-020 P1-2：取消不是连接失败——不得触发重连循环（修复前 OCE 被吞并送入 HandleConnectFailure）
        var inner = new FakeMqttInnerClient();
        await using var wrapper = new MqttClientWrapper(
            FastReconnectOptions(maxAttempts: 3), NullLogger<MqttClientWrapper>.Instance, inner, []);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => wrapper.ConnectAsync(cts.Token));

        // 取消后回落到 Disconnected；若错误启动重连循环，状态会进入 Reconnecting/Connected
        Assert.Equal(MqttConnectionState.Disconnected, wrapper.State);
        await Task.Delay(150);
        Assert.Equal(MqttConnectionState.Disconnected, wrapper.State);
        Assert.Equal(1, inner.ConnectCalls);
    }

    [Fact]
    public async Task Disable_DisconnectsAndStopsReconnect()
    {
        // ADR-061：关闭开关 → 断开连接 + 置 Disabled + 意外断开不再重连
        var inner = new FakeMqttInnerClient();
        var toggle = new ToggleFake();
        await using var wrapper = new MqttClientWrapper(
            FastReconnectOptions(), NullLogger<MqttClientWrapper>.Instance, inner, [], toggle);

        Assert.True((await wrapper.ConnectAsync()).IsSuccess);
        Assert.True(inner.IsConnected);
        await wrapper.SubscribeAsync("t", 1);

        await toggle.SetEnabledAsync(false);
        await WaitUntilAsync(() => wrapper.State == MqttConnectionState.Disabled, TimeSpan.FromSeconds(5));

        Assert.False(inner.IsConnected);
        // 已关闭状态下意外断开不触发重连
        var calls = inner.ConnectCalls;
        inner.SimulateDrop();
        await Task.Delay(300);
        Assert.Equal(calls, inner.ConnectCalls);
        Assert.Equal(MqttConnectionState.Disabled, wrapper.State);
    }

    [Fact]
    public async Task Enable_AfterDisable_Reconnects()
    {
        // ADR-061：开关重开 → 自动恢复连接（订阅由 CleanStart 重放兜底）
        var inner = new FakeMqttInnerClient();
        var toggle = new ToggleFake();
        await using var wrapper = new MqttClientWrapper(
            FastReconnectOptions(), NullLogger<MqttClientWrapper>.Instance, inner, [], toggle);

        Assert.True((await wrapper.ConnectAsync()).IsSuccess);
        await wrapper.SubscribeAsync("t", 1);

        await toggle.SetEnabledAsync(false);
        await WaitUntilAsync(() => wrapper.State == MqttConnectionState.Disabled, TimeSpan.FromSeconds(5));

        await toggle.SetEnabledAsync(true);
        await WaitUntilAsync(() => wrapper.State == MqttConnectionState.Connected, TimeSpan.FromSeconds(5));
        Assert.True(inner.IsConnected);
    }

    [Fact]
    public async Task ConnectAsync_WhenDisabled_ReturnsFailureAndStaysDisabled()
    {
        // ADR-061：开关关闭时 ConnectAsync 直接失败——不置 Connecting、不触发重连
        var inner = new FakeMqttInnerClient();
        var toggle = new ToggleFake { IsEnabled = false };
        await using var wrapper = new MqttClientWrapper(
            FastReconnectOptions(), NullLogger<MqttClientWrapper>.Instance, inner, [], toggle);

        var r = await wrapper.ConnectAsync();

        Assert.True(r.IsFailure);
        Assert.Equal(MqttConnectionState.Disabled, wrapper.State);
        Assert.Equal(0, inner.ConnectCalls);
        await Task.Delay(200);
        Assert.Equal(0, inner.ConnectCalls);
        Assert.Equal(MqttConnectionState.Disabled, wrapper.State);
    }

    [Fact]
    public async Task DisableDuringReconnect_StopsRetryingAndStaysDisabled()
    {
        // ADR-061：重连循环进行中关闭开关 → 取消重连 + 置 Disabled + 不再尝试连接
        var inner = new FakeMqttInnerClient { ConnectException = new TimeoutException("broker down") };
        var toggle = new ToggleFake();
        await using var wrapper = new MqttClientWrapper(
            FastReconnectOptions(maxAttempts: 10), NullLogger<MqttClientWrapper>.Instance, inner, [], toggle);

        Assert.True((await wrapper.ConnectAsync()).IsFailure);
        // 等待重连循环已跑起来（首连 + 至少一次重试）
        await WaitUntilAsync(() => inner.ConnectCalls >= 2, TimeSpan.FromSeconds(5));

        await toggle.SetEnabledAsync(false);
        await WaitUntilAsync(() => wrapper.State == MqttConnectionState.Disabled, TimeSpan.FromSeconds(5));

        // 等重连循环充分退出后再计数，避免把退出瞬间的在途连接误判为新重试
        await Task.Delay(300);
        var calls = inner.ConnectCalls;
        await Task.Delay(400);
        Assert.Equal(calls, inner.ConnectCalls);
        Assert.Equal(MqttConnectionState.Disabled, wrapper.State);
    }
}
