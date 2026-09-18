using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NitroGateway.Domain.Measurements;
using NitroGateway.Forwarder;
using NitroGateway.Storage.Buffer;
using NitroGateway.Transport.MQTT;
using Xunit;

namespace NitroGateway.IntegrationTests;

[Collection("Forwarder")]
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

    [Fact]
    public async Task FirstRound_RunsImmediately_WithoutWaitingFullInterval()
    {
        var buffer = CreateBacklogBuffer(1001);
        var mqtt = new FakeMqttClient { State = MqttConnectionState.Disconnected };
        var logger = new CapturingLogger<ForwarderEngine>();
        await using var provider = BuildProvider(buffer, mqtt);

        var engine = new ForwarderEngine(
            provider.GetRequiredService<IServiceScopeFactory>(),
            TimeSpan.FromSeconds(10),
            buffer,
            logger);

        await engine.StartAsync(CancellationToken.None);
        try
        {
            await WaitForAsync(() => WarningCount(logger) == 1, TimeSpan.FromSeconds(2));
        }
        finally
        {
            await engine.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task StopAsync_WithConnectedMqtt_DrainsRemainingBuffer()
    {
        // 启动时缓冲为空且 MQTT 断开（首轮跳过），等引擎真正进入运行态后再注入停机现场，
        // 避免 StartAsync 内部 Task.Run 启动即 StopAsync 的调度竞态（.NET 10 BackgroundService）
        var buffer = CreateBacklogBuffer(0);
        var mqtt = new FakeMqttClient { State = MqttConnectionState.Disconnected };
        var logger = new CapturingLogger<ForwarderEngine>();

        var services = new ServiceCollection();
        services.AddSingleton<IForwardBuffer>(buffer);
        services.AddSingleton<IMqttClient>(mqtt);
        services.AddSingleton<IMessageSerializer, JsonMessageSerializer>();
        services.AddSingleton<IForwarder>(sp => new NitroGateway.Forwarder.Forwarder(
            buffer,
            sp.GetRequiredService<IMessageSerializer>(),
            mqtt,
            NullLogger<NitroGateway.Forwarder.Forwarder>.Instance));
        await using var provider = services.BuildServiceProvider();

        var engine = new ForwarderEngine(
            provider.GetRequiredService<IServiceScopeFactory>(),
            TimeSpan.FromSeconds(10), // 大间隔：测试期间不会触发中间 tick
            buffer,
            logger);

        await engine.StartAsync(CancellationToken.None);
        try
        {
            // .NET 10 BackgroundService.StartAsync 用 Task.Run 调度 ExecuteAsync（不再同步执行）。
            // 若在委托真正启动前 StopAsync 取消令牌，Task.Run 直接返回 Canceled 任务且引擎体从未运行，
            // 停机排空不会发生（本地并行 slnx 下 ~50% 复现，ExecuteTask=[Canceled]、日志为空）。
            await WaitForAsync(() => logger.Entries.Any(e => e.Message.Contains("ForwarderEngine Started.")), TimeSpan.FromSeconds(5));

            // 停机瞬间现场：缓冲有 3 批待发，MQTT 仍连接
            buffer.Pending.AddRange(CreateBacklogBuffer(3).Pending);
            mqtt.State = MqttConnectionState.Connected;

            await engine.StopAsync(CancellationToken.None);
        }
        finally
        {
            await engine.StopAsync(CancellationToken.None);
        }

        // 但跨测试进程并行/高负载下，取消 → drain 的衔接可能被调度延后（本地复现偶发 flaky），
        // 这里条件等待 drain 结果就绪再断言，避免对调度窗口的脆弱假设。
        await WaitForAsync(() => buffer.Pending.Count == 0 && mqtt.Published.Count > 0, TimeSpan.FromSeconds(5));

        Assert.Empty(buffer.Pending);
        Assert.NotEmpty(mqtt.Published);
    }

    [Fact]
    public async Task BacklogQueryFailure_DoesNotStopEngine()
    {
        var buffer = CreateBacklogBuffer(0);
        buffer.GetCountError = new InvalidOperationException("模拟数据库瞬时故障");
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
            await Task.Delay(200);

            Assert.False(engine.ExecuteTask?.IsFaulted, "积压查询异常不应让引擎 fault");
            Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("积压"));
        }
        finally
        {
            await engine.StopAsync(CancellationToken.None);
        }
    }
}
