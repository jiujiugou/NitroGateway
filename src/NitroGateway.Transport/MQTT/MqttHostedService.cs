using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace NitroGateway.Transport.MQTT;

/// <summary>MQTT 连接 & 状态监控。应用启动时建立连接，监听 StateChanged 记录日志</summary>
internal sealed class MqttHostedService : BackgroundService
{
    private readonly IMqttClient _mqtt;
    private readonly ILogger<MqttHostedService> _logger;

    public MqttHostedService(IMqttClient mqtt, ILogger<MqttHostedService> logger)
    {
        _mqtt = mqtt;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _mqtt.StateChanged += OnStateChanged;

        var r = await _mqtt.ConnectAsync(ct);
        if (r.IsSuccess)
            await _mqtt.SubscribeAsync("nitrogateway/+/cmd", ct: ct);

        _logger.LogInformation("MQTT 首次连接结果: {Result}", r.IsSuccess ? "成功" : r.Error!.Message);
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
                _logger.LogError("MQTT 重连失败，已达最大重试次数");
                break;
        }
    }

    public override void Dispose()
    {
        _mqtt.StateChanged -= OnStateChanged;
        base.Dispose();
    }
}
