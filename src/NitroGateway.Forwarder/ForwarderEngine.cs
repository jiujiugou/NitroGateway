using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NitroGateway.Storage.Buffer;
using NitroGateway.Transport.MQTT;

namespace NitroGateway.Forwarder;

/// <summary>
/// 转发引擎。BackgroundService + PeriodicTimer，定时触发转发。
/// </summary>
public sealed class ForwarderEngine : BackgroundService
{
    /// <summary>积压告警阈值（批），超过此值时记 Warning</summary>
    private const int BacklogWarningThreshold = 1000;

    /// <summary>单轮最大排水量（批），防止 MQTT 恢复瞬间冲垮 Broker</summary>
    private const int MaxDrainPerRound = 2000;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeSpan _interval;
    private readonly IForwardBuffer _buffer;
    private readonly ILogger<ForwarderEngine> _logger;

    /// <summary>创建转发引擎</summary>
    public ForwarderEngine(
        IServiceScopeFactory scopeFactory,
        TimeSpan interval,
        IForwardBuffer buffer,
        ILogger<ForwarderEngine> logger)
    {
        _scopeFactory = scopeFactory;
        _interval = interval;
        _buffer = buffer;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_interval);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                // ── 积压检查 ──
                var backlog = _buffer.Count;
                if (backlog > BacklogWarningThreshold)
                {
                    _logger.LogWarning(
                        "转发缓冲区积压过高: {Count} 批（阈值 {Threshold}），MQTT 恢复后 throttled drain 将分批排水",
                        backlog, BacklogWarningThreshold);
                }

                using var scope = _scopeFactory.CreateScope();
                var mqtt = scope.ServiceProvider.GetRequiredService<IMqttClient>();

                // MQTT 未连接，跳过本轮
                if (mqtt.State != MqttConnectionState.Connected)
                    continue;

                var forwarder = scope.ServiceProvider.GetRequiredService<IForwarder>();

                try
                {
                    // 限制单轮排水量，超出部分下轮继续
                    await forwarder.ForwardBatchAsync(MaxDrainPerRound, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "转发循环发生异常");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host 正常停止
        }

        _logger.LogInformation("ForwarderEngine Stopped.");
    }
}
