using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NitroGateway.Domain.Measurements;
using NitroGateway.Forwarder;
using NitroGateway.Storage.Buffer;
using NitroGateway.Transport.MQTT;
using Xunit;

namespace NitroGateway.IntegrationTests;

/// <summary>
/// ForwarderEngine 积压告警限流测试（ADR-001 P2-8）：
/// 长断线期间每轮都会检查积压，告警必须限流——首次超限立即告警，之后每 60s 一次，
/// 积压回落后重置限流状态，避免刷屏。
/// </summary>
public class ForwarderEngineTests
{
    private static FakeForwardBuffer CreateBacklogBuffer(int count)
    {
        var buffer = new FakeForwardBuffer();
        for (var i = 0; i < count; i++)
        {
            buffer.Pending.Add(new BatchMeasurements { Id = Guid.NewGuid(), DeviceId = Guid.NewGuid() });
        }
        return buffer;
    }

    private static int WarningCount(CapturingLogger<ForwarderEngine> logger)
        => logger.Entries.Count(e => e.Level == LogLevel.Warning && e.Message.Contains("积压"));

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        Assert.True(condition(), "等待条件超时");
    }

    private static ServiceProvider BuildProvider(FakeForwardBuffer buffer, FakeMqttClient mqtt)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IForwardBuffer>(buffer);
        services.AddSingleton<IMqttClient>(mqtt);
        return services.BuildServiceProvider();
    }

    /// <summary>持续超限时告警限流：首次立即告警，之后不随每轮重复刷</summary>
    [Fact]
    public async Task BacklogWarning_WhileOverThreshold_IsRateLimited()
    {
        var buffer = CreateBacklogBuffer(1001);
        var mqtt = new FakeMqttClient { State = MqttConnectionState.Disconnected };
        var logger = new CapturingLogger<ForwarderEngine>();
        await using var provider = BuildProvider(buffer, mqtt);

        var engine = new ForwarderEngine(
            provider.GetRequiredService<IServiceScopeFactory>(),
            TimeSpan.FromMilliseconds(20),
            buffer,
            logger);

        await engine.StartAsync(CancellationToken.None);
        try
        {
            await WaitForAsync(() => WarningCount(logger) == 1, TimeSpan.FromSeconds(5));

            // 继续运行多轮，告警数保持 1，不再每轮刷屏
            await Task.Delay(200);
            Assert.Equal(1, WarningCount(logger));
        }
        finally
        {
            await engine.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>积压回落后重置限流状态，再次超限立即再告警</summary>
    [Fact]
    public async Task BacklogWarning_AfterRecovery_WarnsImmediatelyAgain()
    {
        var buffer = CreateBacklogBuffer(1001);
        var mqtt = new FakeMqttClient { State = MqttConnectionState.Disconnected };
        var logger = new CapturingLogger<ForwarderEngine>();
        await using var provider = BuildProvider(buffer, mqtt);

        var engine = new ForwarderEngine(
            provider.GetRequiredService<IServiceScopeFactory>(),
            TimeSpan.FromMilliseconds(20),
            buffer,
            logger);

        await engine.StartAsync(CancellationToken.None);
        try
        {
            await WaitForAsync(() => WarningCount(logger) == 1, TimeSpan.FromSeconds(5));

            // 积压回落（引擎跑几轮后观察到 Count ≤ 阈值并重置限流状态）
            buffer.Pending.Clear();
            await Task.Delay(150);

            // 再次超限：应立即再告警，无需等 60s
            for (var i = 0; i < 1001; i++)
                buffer.Pending.Add(new BatchMeasurements { Id = Guid.NewGuid(), DeviceId = Guid.NewGuid() });

            await WaitForAsync(() => WarningCount(logger) == 2, TimeSpan.FromSeconds(5));
        }
        finally
        {
            await engine.StopAsync(CancellationToken.None);
        }
    }
}
