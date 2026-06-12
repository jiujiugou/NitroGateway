namespace NitroGateway.Domain.Devices;

/// <summary>
/// 协议标识值对象，用于替代魔法字符串。
/// 基于协议名称和版本号判断相等性，忽略大小写。
/// </summary>
public sealed class ProtocolIdentifier
{
    /// <summary>协议名称，如 "Modbus"、"OPC UA"、"S7"</summary>
    public required string Name { get; init; }

    /// <summary>协议方言，如 "Modbus RTU"、"Modbus TCP"</summary>
    public string? Dialect { get; init; }

    /// <inheritdoc />
    public override bool Equals(object? obj) =>
        obj is ProtocolIdentifier other
        && string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Dialect, other.Dialect, StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public override int GetHashCode() =>
        HashCode.Combine(Name.ToLowerInvariant(), Dialect?.ToLowerInvariant());

    /// <summary>返回 "Name/Dialect" 或 "Name"（无方言时）</summary>
    public override string ToString() =>
        Dialect is not null ? $"{Name}/{Dialect}" : Name;

    // ---- 预置常用协议 ----

    /// <summary>Modbus RTU / TCP</summary>
    public static readonly ProtocolIdentifier Modbus = new() { Name = "Modbus" };

    /// <summary>OPC Unified Architecture</summary>
    public static readonly ProtocolIdentifier OpcUa = new() { Name = "OPC UA" };

    /// <summary>Siemens S7 系列 PLC</summary>
    public static readonly ProtocolIdentifier S7 = new() { Name = "S7" };

    /// <summary>未知协议，用于未指定时的默认值</summary>
    public static readonly ProtocolIdentifier Unknown = new() { Name = "Unknown" };
}
