namespace NitroGateway.Telemetry.Tracing;

/// <summary>
/// 统一 Activity 名称常量。所有 Span 创建必须使用此处定义的名称，不得在业务代码中写字符串。
/// </summary>
public static class GatewayActivities
{
    /// <summary>一轮完整采集（CollectionEngine）</summary>
    public const string CollectRound = "CollectRound";

    /// <summary>单台设备采集（DeviceCollector）</summary>
    public const string CollectDevice = "CollectDevice";

    /// <summary>从设备读取原始数据（DeviceReader）</summary>
    public const string ReadDevice = "ReadDevice";

    /// <summary>值转换管道（PointValuePipeline）</summary>
    public const string Pipeline = "Pipeline";

    /// <summary>数据分发——双写时序库+缓冲（DataDispatcher）</summary>
    public const string Dispatch = "Dispatch";

    /// <summary>数据转发（Forwarder）</summary>
    public const string Forward = "Forward";

    /// <summary>SQLite 写入操作（SqliteMeasurementStore / SqliteForwardBuffer）</summary>
    public const string SqliteWrite = "SqliteWrite";

    /// <summary>MQTT 发布操作（MqttClientWrapper）</summary>
    public const string MqttPublish = "MqttPublish";
}
