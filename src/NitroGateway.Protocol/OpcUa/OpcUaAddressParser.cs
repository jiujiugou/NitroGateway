using System.Globalization;

namespace NitroGateway.Protocols.OpcUa;

/// <summary>OPC UA 地址解析器</summary>
public sealed class OpcUaAddressParser : IAddressParser
{
    /// <summary>
    /// 解析地址字符串为 <see cref="OpcUaAddress"/>：支持四种标识符
    /// （<c>ns=N;s=字符串</c> / <c>ns=N;i=数字</c> / <c>ns=N;g=GUID</c> / <c>ns=N;b=base64</c>）。
    /// 只按第一个 <c>;</c> 切分，避免字符串标识符内含 <c>;</c> 被切碎。格式非法抛 <see cref="ArgumentException"/>。
    /// </summary>
    public PointAddress Parse(string rawAddress)
    {
        if (string.IsNullOrWhiteSpace(rawAddress))
            throw new ArgumentException("地址不能为空", nameof(rawAddress));

        // ns=3;s=Temperature  or  ns=2;i=1001
        // 只按第一个 ';' 切成两段：字符串标识符本身可能含 ';'（如 ns=3;s=a;b），
        // 用 Split(';') 会把 id 切碎、破坏 Serialize↔Parse 往返（属性测试发现）。
        var parts = rawAddress.Split(';', 2);
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
            try
            {
                return new OpcUaAddress { Raw = rawAddress, NamespaceIndex = ns, OpaqueId = Convert.FromBase64String(base64) };
            }
            catch (FormatException)
            {
                // 非法 base64 归为地址格式错误；不把 FormatException 实现细节泄漏给调用方（属性测试发现）。
                throw new ArgumentException($"无法解析 OpaqueId(Base64): {idPart}", nameof(rawAddress));
            }
        }

        throw new ArgumentException($"不支持的 OPC UA 地址格式: {rawAddress}");
    }

    /// <summary>把 <see cref="OpcUaAddress"/> 序列化为 <c>"ns=N;..."</c> 字符串（与 <see cref="Parse"/> 往返一致）；非 OPC UA 地址抛 <see cref="ArgumentException"/>。</summary>
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

    /// <summary>恒返回 -1：OPC UA 没有"连续地址"概念，不支持按区间合并读取（批量读按 NodeId 列表）。</summary>
    public int GetDistance(PointAddress a, PointAddress b) => -1;
}
