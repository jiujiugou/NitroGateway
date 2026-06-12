using NitroGateway.Domain.Devices;

namespace NitroGateway.Domain.Measurements;

/// <summary>
/// 测点记录，代表某个点位在一次采集中产生的完整数据。
/// 相比 <see cref="PointSnapshot"/>，本记录可脱离 <see cref="DevicePoint"/> 独立存储和传输，
/// 包含了查询和转发所需的全部上下文信息。
/// </summary>
public sealed class MeasurementRecord
{
    /// <summary>记录唯一标识</summary>
    public Guid Id { get; init; }

    /// <summary>所属设备 ID</summary>
    public Guid DeviceId { get; init; }

    /// <summary>所属点位 ID</summary>
    public Guid DevicePointId { get; init; }

    /// <summary>点位名称（冗余字段，便于日志和展示，不需要反查 DevicePoint）</summary>
    public required string PointName { get; init; }

    /// <summary>采集到的值</summary>
    public object? Value { get; init; }

    /// <summary>数据类型（冗余字段，便于下游解析值时确定类型）</summary>
    public DataType DataType { get; init; }

    /// <summary>采集时间戳（数据源时间或设备本地时间）</summary>
    public DateTime Timestamp { get; init; }

    /// <summary>网关接收到该数据的时间</summary>
    public DateTime ReceivedAt { get; init; }

    /// <summary>数据质量标记</summary>
    public QualityCode Quality { get; init; } = QualityCode.Good;
}
