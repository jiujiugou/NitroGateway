using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace NitroGateway.Transport.MQTT;

/// <summary>
/// MQTT 连接监督（ADR-006 P1-3/P3-3）。应用生命周期内维持与 Broker 的连接：
/// 启动时负责首连；之后按 <see cref="MqttConnectionOptions.ReconnectMaxIntervalMs"/> 周期监督，
/// 状态为 Disconnected（首连失败）或 Faulted（快速重连放弃）时兜底重连，Broker 恢复后无需重启网关。
/// 意外断线后的指数退避快速重连由 <see cref="MqttClientWrapper"/> 内部完成，此处不重复触发。
/// </summary>
internal sealed class MqttHostedService : BackgroundService
{
    private readonly IMqttClient _mqtt;
    private readonly MqttConnectionOptions _options;
    private readonly ILogger<MqttHostedService> _logger;

    public MqttHostedService(IMqttClient mqtt, MqttConnectionOptions options, ILogger<MqttHostedService> logger)
    {
        _mqtt = mqtt;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _mqtt.StateChanged += OnStateChanged;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // ADR-006 P1-3：Faulted 时兜底重连；Disconnected 仅在配置了自动重连时兜底
                //（MaxReconnectAttempts=0 语义为"不自动重连"，监督循环不越权）。
                if (_mqtt.State is MqttConnectionState.Faulted
                    || (_mqtt.State is MqttConnectionState.Disconnected && _options.MaxReconnectAttempts > 0))
                {
                    try
                    {
                        var r = await _mqtt.ConnectAsync(ct);
                        if (r.IsFailure)
                            // ADR-020 P3-1：监督重连失败明细降 Debug——broker 长期不可用时每周期刷 Warning
                            // 属于刷屏（与 ADR-016 P2-1 热路径日志降级目标相悖）；故障已由状态机与指标表达。
                            _logger.LogDebug("MQTT 监督重连失败: {Error}，{Interval}ms 后重试",
                                r.Error?.Message, _options.ReconnectMaxIntervalMs);
                    }
                    catch (OperationCanceledException)
                    {
                        // ADR-020 P1-2：ConnectAsync 在取消时上抛 OCE（不再吞掉），停机时正常退出监督循环
                        break;
                    }
                }

                try
                {
                    await Task.Delay(_options.ReconnectMaxIntervalMs, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        finally
        {
            _mqtt.StateChanged -= OnStateChanged;
        }
    }

    private void OnStateChanged(MqttConnectionState state)
    {
        switch (state)
        {
            case MqttConnectionState.Connected:
                _logger.LogInformation("MQTT 已连接");
                break;
            case MqttConnectionState.Disconnected:
                _logger.LogWarning("MQTT 已断开，转发暂停");
                break;
            case MqttConnectionState.Reconnecting:
                _logger.LogInformation("MQTT 正在重连...");
                break;
            case MqttConnectionState.Faulted:
                // ADR-020 P3-1：Faulted 每轮监督重连都会再触发一次，长期断线会刷屏——降 Warning 保留存在感
                _logger.LogWarning("MQTT 重连失败，已达最大重试次数，监督循环将继续尝试");
                break;
        }
    }

    public override void Dispose()
    {
        _mqtt.StateChanged -= OnStateChanged;
        base.Dispose();
    }
}
