namespace NitroGateway.Protocol.OpcUa;

/// <summary>OPC UA NodeId 地址。直接对应 OPC UA NodeId 规范，不存裸 string</summary>
public sealed record OpcUaAddress : PointAddress
{
    /// <summary>命名空间索引，默认 0</summary>
    public ushort NamespaceIndex { get; init; }

    /// <summary>字符串标识符（ns=3;s=xxx → "xxx"）</summary>
    public string? StringId { get; init; }

    /// <summary>数字标识符（ns=2;i=1001 → 1001）</summary>
    public uint? NumericId { get; init; }

    /// <summary>GUID 标识符（ns=4;g=xxx）</summary>
    public Guid? GuidId { get; init; }

    /// <summary>Opaque 标识符（ns=5;b=xxx）</summary>
    public byte[]? OpaqueId { get; init; }
}
