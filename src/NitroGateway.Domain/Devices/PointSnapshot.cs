namespace NitroGateway.Domain.Devices;

/// <summary>
/// 点位运行时快照，记录一次采集得到的值及其元信息。
/// 每次采集生成新的实例，不可变——不应修改已有快照。
/// 自描述：无需查数据库即可获取 DeviceId、点位名称等上下文。
/// </summary>
public sealed record PointSnapshot
{
    /// <summary>所属设备 ID</summary>
    public Guid DeviceId { get; init; }

    /// <summary>对应的点位定义 ID</summary>
    public Guid DevicePointId { get; init; }

    /// <summary>点位名称（自描述），构造快照时由点位定义填充，云端上报与告警可直接使用</summary>
    public string? PointName { get; init; }

    /// <summary>
    /// 点位数据类型（自描述冗余字段，ADR-001 P1-5）。
    /// 构造快照时由 <see cref="DevicePoint.DataType"/> 填充，
    /// 转发 payload 据此携带真实类型，云端不再把 Bool/Int/String 按 Float 解析。
    /// </summary>
    public DataType DataType { get; init; }

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

    /// <summary>
    /// 死区（变化抑制阈值，ADR-053）。由管线从 <see cref="DevicePoint.Deadband"/> 透传；
    /// ChangeDetector 在 Dispatcher 层据此判定「未超死区则抑制落库/转发/推送」。
    /// 0 表示不启用抑制（每样本照写，向后兼容）。
    /// </summary>
    public double Deadband { get; init; }

    /// <summary>质量异常时的错误描述，如 "Modbus 超时"、"CRC 校验失败"。Good 时为 null</summary>
    public string? ErrorMessage { get; init; }
}
