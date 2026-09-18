namespace NitroGateway.Webapi.Models;

public sealed class ForwardMqttEnabledDto
{
    /// <summary>是否启用 MQTT 上云转发（PUT 时作为目标值）</summary>
    public bool Enabled { get; set; }
}
