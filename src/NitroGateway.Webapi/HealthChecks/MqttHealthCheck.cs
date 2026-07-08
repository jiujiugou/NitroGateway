using Microsoft.Extensions.Diagnostics.HealthChecks;
using NitroGateway.Transport.MQTT;

namespace NitroGateway.Webapi.HealthChecks;

/// <summary>MQTT 健康检查：验证 Broker 连接状态</summary>
public sealed class MqttHealthCheck : IHealthCheck
{
    private readonly IMqttClient _mqtt;

    public MqttHealthCheck(IMqttClient mqtt)
    {
        _mqtt = mqtt;
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken ct = default)
    {
        return _mqtt.State switch
        {
            MqttConnectionState.Connected => Task.FromResult(
                HealthCheckResult.Healthy("MQTT 已连接")),

            MqttConnectionState.Connecting or MqttConnectionState.Reconnecting => Task.FromResult(
                HealthCheckResult.Degraded($"MQTT {_mqtt.State}")),

            _ => Task.FromResult(
                HealthCheckResult.Unhealthy($"MQTT {_mqtt.State}"))
        };
    }
}
