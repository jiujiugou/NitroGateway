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

            // ADR-061：转发开关关闭是用户主动操作，非故障——报 Degraded 而非 Unhealthy
            MqttConnectionState.Disabled => Task.FromResult(
                HealthCheckResult.Degraded("MQTT 已关闭（转发开关关闭）")),

            _ => Task.FromResult(
                HealthCheckResult.Unhealthy($"MQTT {_mqtt.State}"))
        };
    }
}
