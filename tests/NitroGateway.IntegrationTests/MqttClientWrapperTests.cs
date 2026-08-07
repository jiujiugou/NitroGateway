using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NitroGateway.Shared;
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
        await using var wrapper = new MqttClientWrapper(FastReconnectOptions(), NullLogger<MqttClientWrapper>.Instance, inner);

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
        await using var wrapper = new MqttClientWrapper(FastReconnectOptions(), NullLogger<MqttClientWrapper>.Instance, inner);

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
        await using var wrapper = new MqttClientWrapper(FastReconnectOptions(maxAttempts: 2), NullLogger<MqttClientWrapper>.Instance, inner);

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
        var wrapper = new MqttClientWrapper(FastReconnectOptions(), NullLogger<MqttClientWrapper>.Instance, inner);

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
        await using var wrapper = new MqttClientWrapper(FastReconnectOptions(), NullLogger<MqttClientWrapper>.Instance, inner);

        Assert.True((await wrapper.ConnectAsync()).IsSuccess);
        inner.SimulateDrop();
        await WaitUntilAsync(() => wrapper.State == MqttConnectionState.Connected, TimeSpan.FromSeconds(5));

        var disconnect = await wrapper.DisconnectAsync();
        Assert.True(disconnect.IsSuccess);
        Assert.Equal(MqttConnectionState.Disconnected, wrapper.State);
    }
}
