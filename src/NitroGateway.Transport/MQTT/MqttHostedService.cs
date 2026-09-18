using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace NitroGateway.Transport.MQTT;

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
                            _logger.LogDebug("MQTT 监督重连失败: {Error}，{Interval}ms 后重试",
                                r.Error?.Message, _options.ReconnectMaxIntervalMs);
                    }
                    catch (OperationCanceledException)
                    {
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
                _logger.LogWarning("MQTT 重连失败，已达最大重试次数，监督循环将继续尝试");
                break;
            case MqttConnectionState.Disabled:
                _logger.LogInformation("MQTT 已关闭（转发开关关闭），暂停连接与重连");
                break;
        }
    }

    public override void Dispose()
    {
        _mqtt.StateChanged -= OnStateChanged;
        base.Dispose();
    }
}
