using NitroGateway.Domain.Devices;

namespace NitroGateway.Domain.Protocols;

/// <summary>
/// 驱动返回的值，已经过协议解码为领域类型（int/float/double/bool/string），
/// 但未经工程缩放（ScaleFactor/ScaleOffset 由 Pipeline 处理）。
/// Pipeline 不感知协议细节，只处理数值。
/// </summary>
public sealed record RawPointValue
{
    /// <summary>对应的点位定义</summary>
    public required DevicePoint Point { get; init; }

    /// <summary>
    /// 协议解码后的值，类型为 int/float/double/bool/string。
    /// Modbus 驱动负责 ushort[]→DataType 转换 + Endian 处理。
    /// OPC UA 驱动负责 Variant→.NET Type 转换。
    /// </summary>
    public required object Value { get; init; }

    /// <summary>数据源时间戳</summary>
    public DateTime Timestamp { get; init; }
}
