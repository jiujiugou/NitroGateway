namespace NitroGateway.Webapi.Models;

/// <summary>MQTT 上云转发开关 DTO（ADR-059，GET/PUT /api/forwarder/enabled）</summary>
public sealed class ForwardMqttEnabledDto
{
    /// <summary>是否启用 MQTT 上云转发（PUT 时作为目标值）</summary>
    public bool Enabled { get; set; }
}
