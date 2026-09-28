using Opc.Ua;

namespace NitroGateway.Protocols.OpcUa;

/// <summary>
/// Browse 变量属性映射：DataType NodeId → 领域类型名、AccessLevel → 访问级别字符串
/// （纯逻辑，无会话/状态，可单测）。从 <c>OpcUaDriver</c> 抽出的无状态纯函数，行为不变。
/// </summary>
internal static class OpcUaBrowseCodec
{
    /// <summary>DataType 属性 NodeId → 前端 DataType 枚举名（仅映射领域支持的 11 种，其余 Unknown）</summary>
    public static string DataTypeName(NodeId typeId)
    {
        if (typeId is null || typeId.IdType != IdType.Numeric || typeId.NamespaceIndex != 0)
            return "Unknown";
        if (typeId == DataTypeIds.Boolean) return "Bool";
        if (typeId == DataTypeIds.Byte) return "Byte";
        if (typeId == DataTypeIds.Int16) return "Int16";
        if (typeId == DataTypeIds.UInt16) return "UInt16";
        if (typeId == DataTypeIds.Int32) return "Int32";
        if (typeId == DataTypeIds.UInt32) return "UInt32";
        if (typeId == DataTypeIds.Int64) return "Int64";
        if (typeId == DataTypeIds.UInt64) return "UInt64";
        if (typeId == DataTypeIds.Float) return "Float";
        if (typeId == DataTypeIds.Double) return "Double";
        if (typeId == DataTypeIds.String) return "String";
        return "Unknown";
    }

    /// <summary>AccessLevel 属性 byte → "Read"/"ReadWrite"/"Write"/"None"</summary>
    public static string AccessToString(byte access)
    {
        var read = (AccessLevels.CurrentRead & access) != 0;
        var write = (AccessLevels.CurrentWrite & access) != 0;
        return read && write ? "ReadWrite" : read ? "Read" : write ? "Write" : "None";
    }
}
