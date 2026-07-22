using Prometheus;

namespace NitroGateway.Telemetry;

/// <summary>
/// 全局指标定义。所有模块通过本类的静态字段上报。
/// 命名规范：nitro_{领域}_{指标名}_{单位后缀}
/// </summary>
public static class NitroMetrics
{
    // ═══════════════════════════════════════════════════════════════
    //  采集
    // ═══════════════════════════════════════════════════════════════

    /// <summary>每设备采集次数。label: device (deviceId), status (success|failure)</summary>
    public static readonly Counter CollectionTotal = Metrics.CreateCounter(
        "nitro_collection_total",
        "每设备采集总次数",
        new CounterConfiguration
        {
            LabelNames = ["device", "status"]
        });

    /// <summary>单轮采集耗时（毫秒）</summary>
    public static readonly Histogram CollectionDurationMs = Metrics.CreateHistogram(
        "nitro_collection_duration_ms",
        "单轮采集耗时（毫秒）",
        new HistogramConfiguration
        {
            Buckets = [5, 10, 25, 50, 100, 250, 500, 1000, 2500, 5000]
        });

    /// <summary>设备熔断器状态。label: device (deviceId)。0=Closed, 1=Open, 2=HalfOpen</summary>
    public static readonly Gauge CircuitBreakerState = Metrics.CreateGauge(
        "nitro_circuit_breaker_state",
        "设备熔断器状态 0=Closed 1=Open 2=HalfOpen",
        new GaugeConfiguration
        {
            LabelNames = ["device"]
        });

    // ═══════════════════════════════════════════════════════════════
    //  转发
    // ═══════════════════════════════════════════════════════════════

    /// <summary>转发次数。label: status (success|failure|deadletter)</summary>
    public static readonly Counter ForwardTotal = Metrics.CreateCounter(
        "nitro_forward_total",
        "转发总次数",
        new CounterConfiguration
        {
            LabelNames = ["status"]
        });

    /// <summary>转发缓冲积压批次数</summary>
    public static readonly Gauge BufferBacklog = Metrics.CreateGauge(
        "nitro_buffer_backlog",
        "转发缓冲中待处理的批次数");

    /// <summary>当前节流批量大小（反映 MQTT 健康度）</summary>
    public static readonly Gauge ThrottleBatchSize = Metrics.CreateGauge(
        "nitro_throttle_batch_size",
        "自适应节流器当前批量大小");

    // ═══════════════════════════════════════════════════════════════
    //  连接
    // ═══════════════════════════════════════════════════════════════

    /// <summary>MQTT 连接状态。0=Disconnected, 1=Connecting, 2=Connected, 3=Reconnecting, 4=Faulted</summary>
    public static readonly Gauge MqttState = Metrics.CreateGauge(
        "nitro_mqtt_state",
        "MQTT 连接状态 0=Disconnected 1=Connected 2=Reconnecting 3=Connecting 4=Faulted");

    // ═══════════════════════════════════════════════════════════════
    //  设备
    // ═══════════════════════════════════════════════════════════════

    /// <summary>在线设备数</summary>
    public static readonly Gauge DevicesOnline = Metrics.CreateGauge(
        "nitro_devices_online",
        "当前在线设备数");

    /// <summary>可用设备数</summary>
    public static readonly Gauge DevicesAvailable = Metrics.CreateGauge(
        "nitro_devices_available",
        "当前可用设备数");
}
