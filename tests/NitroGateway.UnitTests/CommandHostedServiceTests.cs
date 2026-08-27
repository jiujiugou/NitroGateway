using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NitroGateway.Command;
using NitroGateway.DeviceManagement;
using NitroGateway.Shared;
using NitroGateway.Transport.MQTT;
using Xunit;

namespace NitroGateway.UnitTests;

/// <summary>
/// CommandHostedService 测试（ADR-069）：启动时若已连接则订阅一次；
/// 注入 commands 消息后驱动「解析 → 处理 → 回执」。
/// </summary>
public sealed class CommandHostedServiceTests
{
    private const string SiteId = "site-a";
    private static readonly Guid DeviceId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid PointId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid CommandId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static IConfiguration Config() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { [SiteOptions.IdKey] = SiteId })
        .Build();

    private static CommandHostedService Make(out FakeWriteService write, out RecordingFakeMqttClient mqtt)
    {
        write = new FakeWriteService();
        mqtt = new RecordingFakeMqttClient { State = MqttConnectionState.Connected };
        var processor = new CommandProcessor(write, mqtt, NullLogger<CommandProcessor>.Instance);
        return new CommandHostedService(mqtt, processor, Config(), NullLogger<CommandHostedService>.Instance);
    }

    [Fact]
    public async Task ExecuteAsync_WhenAlreadyConnected_SubscribesOnce()
    {
        var host = Make(out _, out var mqtt);
        await host.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(() => mqtt.SubscribedTopics.Count > 0);
            Assert.Contains(CommandHostedService.CommandsSubscription, mqtt.SubscribedTopics);
            Assert.Single(mqtt.SubscribedTopics);
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ExecuteAsync_CommandMessage_DrivesWriteAndAck()
    {
        var host = Make(out var write, out var mqtt);
        await host.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(() => mqtt.SubscribedTopics.Count > 0);
            mqtt.Push($"nitrogateway/{SiteId}/{DeviceId}/commands",
                Encoding.UTF8.GetBytes($"{{\"commandId\":\"{CommandId}\",\"type\":\"WritePoint\",\"pointId\":\"{PointId}\",\"value\":42}}"));

            await WaitUntilAsync(() => mqtt.Published.Count > 0);

            Assert.Single(write.Requests);
            Assert.Equal(PointId, write.Requests[0].PointId);
            Assert.Equal(42L, write.Requests[0].Value);
            Assert.EndsWith("/commands/ack", mqtt.Published[0].Topic);
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
                throw new TimeoutException("等待命令处理超时");
            await Task.Delay(20);
        }
    }
}
