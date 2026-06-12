using NitroGateway.Domain.Devices;

namespace NitroGateway.Domain.Protocols;

/// <summary>
/// 驱动返回的原始数据，未经类型转换和缩放。
/// 由 Collection.PointValuePipeline 消费。
/// </summary>
public sealed class RawPointValue
{
    /// <summary>对应的点位定义</summary>
    public required DevicePoint Point { get; init; }

    /// <summary>
    /// 协议返回的原始数据，类型由各协议自行约定：
    /// Modbus → ushort[]（寄存器值，未做 Endian 处理）
    /// OPC UA → 协议原生 Variant 类型
    /// S7    → byte[]
    /// </summary>
    public required object RawData { get; init; }

    /// <summary>数据源时间戳</summary>
    public DateTime Timestamp { get; init; }
}
