namespace NitroGateway.Domain.Devices;

/// <summary>
/// 点位运行时快照，记录一次采集得到的值及其元信息。
/// 每次采集生成新的实例，不可变——不应修改已有快照。
/// 自描述：无需查数据库即可获取 DeviceId、点位名称等上下文。
/// </summary>
public sealed class PointSnapshot
{
    /// <summary>所属设备 ID</summary>
    public Guid DeviceId { get; init; }

    /// <summary>对应的点位定义 ID</summary>
    public Guid DevicePointId { get; init; }

    /// <summary>
    /// 驱动返回的原始值，未经缩放处理。
    /// 保留此字段用于现场调试（"PLC 到底返回了什么？"）。
    /// 示例：PLC 返回 Int16=1234 → RawValue=1234, Value=123.4（ScaleFactor=0.1）
    /// </summary>
    public object? RawValue { get; init; }

    /// <summary>
    /// 工程值，已经过缩放处理（RawValue × ScaleFactor + ScaleOffset）。
    /// 类型由对应 <see cref="DevicePoint.DataType"/> 决定。
    /// </summary>
    public object? Value { get; init; }

    /// <summary>数据源时间戳（设备本地时间或 PLC 时间）</summary>
    public DateTime Timestamp { get; init; }

    /// <summary>数据质量标记</summary>
    public QualityCode Quality { get; init; } = QualityCode.Good;

    /// <summary>质量异常时的错误描述，如 "Modbus 超时"、"CRC 校验失败"。Good 时为 null</summary>
    public string? ErrorMessage { get; init; }
}
