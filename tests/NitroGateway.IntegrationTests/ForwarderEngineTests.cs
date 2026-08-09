using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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

    /// <summary>ADR-001 P3-12：首轮立即执行，不等第一个周期 tick（周期 10s 时告警应在 2s 内出现）</summary>
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

    /// <summary>ADR-016 P1-1：停机时 MQTT 仍连接，排空剩余缓冲后再退出</summary>
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
        services.AddSingleton<ForwardingThrottle>();
        services.AddSingleton<IMessageSerializer, JsonMessageSerializer>();
        services.AddSingleton<IForwarder>(sp => new NitroGateway.Forwarder.Forwarder(
            buffer,
            sp.GetRequiredService<IMessageSerializer>(),
            mqtt,
            sp.GetRequiredService<ForwardingThrottle>(),
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
            // StartAsync 直接调用 ExecuteAsync（同步跑完首轮后挂起在周期等待），等待任务已创建且未结束即可；
            // 该等待兼作防御：若运行时实现改为 Task.Run 调度，也能覆盖调度窗口
            await WaitForAsync(() => engine.ExecuteTask is { } t && !t.IsCompleted, TimeSpan.FromSeconds(5));

            // 停机瞬间现场：缓冲有 3 批待发，MQTT 仍连接
            buffer.Pending.AddRange(CreateBacklogBuffer(3).Pending);
            mqtt.State = MqttConnectionState.Connected;

            await engine.StopAsync(CancellationToken.None);
        }
        finally
        {
            await engine.StopAsync(CancellationToken.None);
        }

        Assert.Empty(buffer.Pending);
        Assert.NotEmpty(mqtt.Published);
    }

    /// <summary>
    /// ADR-017 P1-1：积压查询瞬时故障不能放倒引擎（BackgroundService 未捕获异常默认 StopHost）——
    /// 记 Error 跳过本轮，引擎继续按周期运行。
    /// </summary>
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
