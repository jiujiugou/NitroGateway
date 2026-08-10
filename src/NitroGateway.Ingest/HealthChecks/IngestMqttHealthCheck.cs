using Microsoft.Extensions.Diagnostics.HealthChecks;
using NitroGateway.Transport.MQTT;

namespace NitroGateway.Ingest.HealthChecks;

/// <summary>中心 MQTT 连接健康检查：Connected 才健康（与 Webapi MqttHealthCheck 同口径）</summary>
public sealed class IngestMqttHealthCheck : IHealthCheck
{
    private readonly IMqttClient _mqtt;

    public IngestMqttHealthCheck(IMqttClient mqtt) => _mqtt = mqtt;

    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
        => Task.FromResult(_mqtt.State == MqttConnectionState.Connected
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy($"MQTT 未连接，当前状态: {_mqtt.State}"));
}
