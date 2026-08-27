using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NitroGateway.Desktop.Services.Connectivity;
using NitroGateway.Shared;
using NitroGateway.Transport.MQTT;
using Xunit;

namespace NitroGateway.UnitTests;

/// <summary>
/// ADR-067：MQTT 连接测试服务——Connect + 发布测试消息双验（防假阳性），
/// 独立临时客户端不碰运行中连接；成功/连接失败/发布失败/超时各路径。
/// </summary>
public sealed class MqttConnectionTesterTests
{
    [Fact]
    public async Task Success_connects_and_publishes_test_message()
    {
        var client = new RecordingMqttClient();
        var tester = CreateTester(_ => client);

        var result = await tester.TestAsync("broker.local", 1883, false, "user", "pass");

        Assert.True(result.Success);
        Assert.Null(result.Message);
        Assert.True(result.ElapsedMs >= 0);
        Assert.Equal("nitrogateway/site-1/connection-test", client.LastTopic);
        Assert.True(client.DisconnectCalled);
    }

    [Fact]
    public async Task Connect_failure_returns_error_message()
    {
        var client = new RecordingMqttClient
        {
            ConnectResult = OperationResult.Failure(OperationalError.Communication("拒绝连接"))
        };
        var tester = CreateTester(_ => client);

        var result = await tester.TestAsync("broker.local", 1883, false, null, null);

        Assert.False(result.Success);
        Assert.Contains("拒绝连接", result.Message);
        Assert.Null(client.LastTopic); // 连接失败不发布
    }

    [Fact]
    public async Task Publish_failure_returns_error_message()
    {
        var client = new RecordingMqttClient
        {
            PublishResult = OperationResult.Failure(OperationalError.General("无权限"))
        };
        var tester = CreateTester(_ => client);

        var result = await tester.TestAsync("broker.local", 1883, false, null, null);

        Assert.False(result.Success);
        Assert.Contains("无权限", result.Message);
        Assert.Equal("nitrogateway/site-1/connection-test", client.LastTopic);
    }

    [Fact]
    public async Task Host_trimmed_anonymous_username_and_no_reconnect()
    {
        var client = new RecordingMqttClient();
        MqttConnectionOptions? captured = null;
        var tester = CreateTester(opts => { captured = opts; return client; });

        var result = await tester.TestAsync("  broker.local  ", 1883, false, "  ", "  ");

        Assert.True(result.Success);
        Assert.Equal("broker.local", captured!.Host);
        Assert.Null(captured.Username); // 空白用户名视为匿名
        Assert.Equal(0, captured.MaxReconnectAttempts); // 测试连接不重连
    }

    [Fact]
    public async Task Unreachable_broker_times_out_with_message()
    {
        var previous = MqttConnectionTester.Timeout;
        MqttConnectionTester.Timeout = TimeSpan.FromMilliseconds(150);
        try
        {
            var tester = CreateTester(_ => new HangingConnectClient());

            var result = await tester.TestAsync("192.0.2.1", 1883, false, null, null);

            Assert.False(result.Success);
            Assert.Contains("超时", result.Message);
        }
        finally
        {
            MqttConnectionTester.Timeout = previous;
        }
    }

    private static MqttConnectionTester CreateTester(Func<MqttConnectionOptions, IMqttClient> factory, string? siteId = "site-1")
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Site:Id"] = siteId })
            .Build();
        return new MqttConnectionTester(
            configuration,
            NullLogger<MqttConnectionTester>.Instance,
            NullLogger<MqttClientWrapper>.Instance,
            factory);
    }

    /// <summary>可编程 Connect/Publish 结果并记录发布 topic 的 IMqttClient 替身。</summary>
    private sealed class RecordingMqttClient : IMqttClient
    {
        public OperationResult ConnectResult { get; init; } = OperationResult.Success();
        public OperationResult PublishResult { get; init; } = OperationResult.Success();
        public OperationResult DisconnectResult { get; init; } = OperationResult.Success();

        public string? LastTopic { get; private set; }
        public bool DisconnectCalled { get; private set; }

        public MqttConnectionState State { get; private set; } = MqttConnectionState.Disconnected;

        public event Action<MqttConnectionState>? StateChanged;

        public IAsyncEnumerable<MqttMessage> Messages => throw new NotSupportedException();

        public Task<OperationResult> ConnectAsync(CancellationToken ct = default)
        {
            if (ConnectResult.IsSuccess)
            {
                State = MqttConnectionState.Connected;
                StateChanged?.Invoke(State);
            }
            return Task.FromResult(ConnectResult);
        }

        public Task<OperationResult> DisconnectAsync(CancellationToken ct = default)
        {
            DisconnectCalled = true;
            State = MqttConnectionState.Disconnected;
            StateChanged?.Invoke(State);
            return Task.FromResult(DisconnectResult);
        }

        public Task<OperationResult> PublishAsync(string topic, byte[] payload, int qos = 1, CancellationToken ct = default)
        {
            LastTopic = topic;
            return Task.FromResult(PublishResult);
        }

        public Task<OperationResult> SubscribeAsync(string topic, int qos = 1, CancellationToken ct = default)
            => Task.FromResult(OperationResult.Success());
    }

    /// <summary>ConnectAsync 挂起直至取消（模拟 broker 不可达，配合缩短超时测超时路径）。</summary>
    private sealed class HangingConnectClient : IMqttClient
    {
        public MqttConnectionState State { get; private set; } = MqttConnectionState.Disconnected;

        public event Action<MqttConnectionState>? StateChanged;

        public IAsyncEnumerable<MqttMessage> Messages => throw new NotSupportedException();

        public async Task<OperationResult> ConnectAsync(CancellationToken ct = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return OperationResult.Success();
        }

        public Task<OperationResult> DisconnectAsync(CancellationToken ct = default)
            => Task.FromResult(OperationResult.Success());

        public Task<OperationResult> PublishAsync(string topic, byte[] payload, int qos = 1, CancellationToken ct = default)
            => Task.FromResult(OperationResult.Success());

        public Task<OperationResult> SubscribeAsync(string topic, int qos = 1, CancellationToken ct = default)
            => Task.FromResult(OperationResult.Success());
    }
}
