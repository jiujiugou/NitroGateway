namespace NitroGateway.Telemetry.Tracing;

/// <summary>
/// 统一 Tag Key 常量。所有 <see cref="System.Diagnostics.Activity.SetTag"/> 调用必须使用此处定义的 Key，
/// 保证跨模块一致，便于下游查询（Jaeger / Grafana / OpenTelemetry）。
/// </summary>
public static class GatewayActivityTags
{
    /// <summary>设备 Guid 字符串</summary>
    public const string DeviceId = "device.id";

    /// <summary>设备名称</summary>
    public const string DeviceName = "device.name";

    /// <summary>设备协议名称（Modbus / OPCUA）</summary>
    public const string DeviceProtocol = "device.protocol";

    /// <summary>本批次快照数量</summary>
    public const string SnapshotCount = "snapshot.count";

    /// <summary>本批次（转发）大小</summary>
    public const string BatchSize = "batch.size";

    /// <summary>采集周期设备总数</summary>
    public const string DeviceCount = "device.count";

    /// <summary>MQTT Topic</summary>
    public const string MqttTopic = "mqtt.topic";

    /// <summary>错误信息</summary>
    public const string ErrorMessage = "error.message";

    /// <summary>数据表名称</summary>
    public const string TableName = "db.table";
}
