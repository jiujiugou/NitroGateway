using Opc.Ua;

namespace NitroGateway.Protocols.OpcUa;

/// <summary>
/// <see cref="OpcUaAddress"/> / <see cref="ExpandedNodeId"/> 与 OPC UA
/// <see cref="NodeId"/> / <c>"ns=N;..."</c> 字符串互转（纯逻辑，无会话/状态，可单测）。
/// <para>从 <c>OpcUaDriver</c> 抽出的无状态纯函数，行为不变。</para>
/// </summary>
internal static class OpcUaNodeIdCodec
{
    /// <summary>OpcUaAddress → OPC UA NodeId（四型标识符映射）</summary>
    public static NodeId ToNodeId(OpcUaAddress addr) => addr switch
    {
        { StringId: { } s } => new NodeId(s, addr.NamespaceIndex),
        { NumericId: { } n } => new NodeId(n, addr.NamespaceIndex),
        { GuidId: { } g } => new NodeId(g, addr.NamespaceIndex),
        { OpaqueId: { } o } => new NodeId(o, addr.NamespaceIndex),
        _ => NodeId.Null
    };

    /// <summary>ExpandedNodeId → "ns=N;..." 格式（与 OpcUaAddressParser.Serialize 一致，可直接回填点位地址）</summary>
    public static string SerializeNodeId(ExpandedNodeId id)
    {
        if (id is null) throw new ArgumentException("浏览结果缺少 NodeId");
        var ns = id.NamespaceIndex;
        var identifier = id.Identifier;
        return identifier switch
        {
            string s => $"ns={ns};s={s}",
            uint u => $"ns={ns};i={u}",
            Guid g => $"ns={ns};g={g}",
            byte[] b => $"ns={ns};b={Convert.ToBase64String(b)}",
            _ => throw new ArgumentException($"不支持的 NodeId 标识符: {identifier}")
        };
    }
}
