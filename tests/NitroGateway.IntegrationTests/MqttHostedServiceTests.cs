using Microsoft.Extensions.Logging.Abstractions;
using NitroGateway.Shared;
using NitroGateway.Transport.MQTT;
using Xunit;

namespace NitroGateway.IntegrationTests;

/// <summary>
/// ADR-006 MqttHostedService 监督循环测试：
/// P1-3 Faulted/首连失败后兜底重连、P2-1 无消费者 cmd 订阅已移除、P3-3 ExecuteAsync 常驻后台监督。
/// </summary>
public class MqttHostedServiceTests
{
    /// <summary>可控 IMqttClient 替身：记录 Connect/Subscribe 调用，ConnectAsync 结果可注入</summary>
    private sealed class ControllableMqttClient : IMqttClient
    {
        public MqttConnectionState State { get; set; }
        public OperationResult ConnectResult { get; set; } = OperationResult.Success();
        public int ConnectCalls { get; private set; }
        public int SubscribeCalls { get; private set; }

        public event Action<MqttConnectionState>? StateChanged;

        public Task<OperationResult> ConnectAsync(CancellationToken ct = default)
        {
            ConnectCalls++;
            if (ConnectResult.IsSuccess) State = MqttConnectionState.Connected;
            return Task.FromResult(ConnectResult);
        }

        public Task<OperationResult> DisconnectAsync(CancellationToken ct = default)
        {
            State = MqttConnectionState.Disconnected;
            return Task.FromResult(OperationResult.Success());
        }

        public Task<OperationResult> PublishAsync(string topic, byte[] payload, int qos = 1, CancellationToken ct = default)
            => Task.FromResult(OperationResult.Success());

        public Task<OperationResult> SubscribeAsync(string topic, int qos = 1, CancellationToken ct = default)
        {
            SubscribeCalls++;
            return Task.FromResult(OperationResult.Success());
        }

        public IAsyncEnumerable<MqttMessage> Messages => EmptyMessages();

        private static async IAsyncEnumerable<MqttMessage> EmptyMessages()
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private static MqttConnectionOptions FastSupervisionOptions() => new()
    {
        Host = "localhost",
        Port = 1883,
        MaxReconnectAttempts = 3,
        ReconnectMaxIntervalMs = 100
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
    public async Task Faulted_IsSupervised_BackToConnected()
    {
        // ADR-006 P1-3：快速重连耗尽进入 Faulted 后，监督循环周期兜底，Broker 恢复即自动连上
        var client = new ControllableMqttClient { State = MqttConnectionState.Faulted };
        var svc = new MqttHostedService(client, FastSupervisionOptions(), NullLogger<MqttHostedService>.Instance);

        await svc.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(() => client.ConnectCalls >= 1, TimeSpan.FromSeconds(5));
            await WaitUntilAsync(() => client.State == MqttConnectionState.Connected, TimeSpan.FromSeconds(5));
        }
        finally
        {
            await svc.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Disconnected_WhenReconnectEnabled_IsSupervised()
    {
        // ADR-006 P1-3：首连失败（Disconnected）由监督循环周期重试，不再是一次性 ExecuteAsync 后放弃
        var client = new ControllableMqttClient
        {
            State = MqttConnectionState.Disconnected,
            ConnectResult = OperationalError.General("broker down")
        };
        var svc = new MqttHostedService(client, FastSupervisionOptions(), NullLogger<MqttHostedService>.Instance);

        await svc.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(() => client.ConnectCalls >= 2, TimeSpan.FromSeconds(5));
        }
        finally
        {
            await svc.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Disabled_IsNotSupervised_NoReconnect()
    {
        // ADR-061：转发开关关闭（Disabled）时监督循环不得重连——关闭即彻底停连，
        // 等待开关重开由 MqttClientWrapper 自行恢复。
        var client = new ControllableMqttClient { State = MqttConnectionState.Disabled };
        var svc = new MqttHostedService(client, FastSupervisionOptions(), NullLogger<MqttHostedService>.Instance);

        await svc.StartAsync(CancellationToken.None);
        try
        {
            // 监督周期 100ms，400ms 内若越权重连会触发 ConnectAsync
            await Task.Delay(400);
            Assert.Equal(0, client.ConnectCalls);
            Assert.Equal(MqttConnectionState.Disabled, client.State);
        }
        finally
        {
            await svc.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task NoConsumerCmdSubscription_IsRemoved()
    {
        // ADR-006 P2-1：nitrogateway/+/cmd 全仓无消费者（云端指令走 HTTP），订阅已移除；
        // 监督循环运行期间不应产生任何 SubscribeAsync 调用
        var client = new ControllableMqttClient { State = MqttConnectionState.Disconnected };
        var svc = new MqttHostedService(client, FastSupervisionOptions(), NullLogger<MqttHostedService>.Instance);

        await svc.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(() => client.ConnectCalls >= 1, TimeSpan.FromSeconds(5));
        }
        finally
        {
            await svc.StopAsync(CancellationToken.None);
        }

        Assert.Equal(0, client.SubscribeCalls);
    }
}
