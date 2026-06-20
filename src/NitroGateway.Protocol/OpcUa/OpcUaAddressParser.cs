using System.Globalization;

namespace NitroGateway.Protocol.OpcUa;

/// <summary>OPC UA 地址解析器</summary>
public sealed class OpcUaAddressParser : IAddressParser
{
    /// <inheritdoc />
    public PointAddress Parse(string rawAddress)
    {
        if (string.IsNullOrWhiteSpace(rawAddress))
            throw new ArgumentException("地址不能为空", nameof(rawAddress));

        // ns=3;s=Temperature  or  ns=2;i=1001
        var parts = rawAddress.Split(';');
        if (parts.Length < 2)
            throw new ArgumentException($"无法解析 OPC UA 地址: {rawAddress}");

        var nsStr = parts[0].Replace("ns=", "");
        if (!ushort.TryParse(nsStr, out var ns))
            throw new ArgumentException($"无法解析 NamespaceIndex: {nsStr}");

        var idPart = parts[1];

        if (idPart.StartsWith("s="))
        {
            return new OpcUaAddress { Raw = rawAddress, NamespaceIndex = ns, StringId = idPart[2..] };
        }

        if (idPart.StartsWith("i="))
        {
            if (!uint.TryParse(idPart[2..], out var numericId))
                throw new ArgumentException($"无法解析 NumericId: {idPart}");
            return new OpcUaAddress { Raw = rawAddress, NamespaceIndex = ns, NumericId = numericId };
        }

        if (idPart.StartsWith("g="))
        {
            if (!Guid.TryParse(idPart[2..], out var guid))
                throw new ArgumentException($"无法解析 GuidId: {idPart}");
            return new OpcUaAddress { Raw = rawAddress, NamespaceIndex = ns, GuidId = guid };
        }

        if (idPart.StartsWith("b="))
        {
            var base64 = idPart[2..];
            return new OpcUaAddress { Raw = rawAddress, NamespaceIndex = ns, OpaqueId = Convert.FromBase64String(base64) };
        }

        throw new ArgumentException($"不支持的 OPC UA 地址格式: {rawAddress}");
    }

    /// <inheritdoc />
    public string Serialize(PointAddress address)
    {
        if (address is not OpcUaAddress ua)
            throw new ArgumentException($"不支持此地址类型: {address.GetType().Name}");

        var idStr = ua switch
        {
            { StringId: { } s } => $"s={s}",
            { NumericId: { } n } => $"i={n}",
            { GuidId: { } g } => $"g={g}",
            { OpaqueId: { } o } => $"b={Convert.ToBase64String(o)}",
            _ => throw new ArgumentException("OPC UA 地址缺少标识符")
        };

        return $"ns={ua.NamespaceIndex};{idStr}";
    }

    /// <inheritdoc />
    public int GetDistance(PointAddress a, PointAddress b) => -1; // OPC UA 没有"连续地址"概念
}
