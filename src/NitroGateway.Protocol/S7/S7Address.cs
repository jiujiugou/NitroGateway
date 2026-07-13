namespace NitroGateway.Protocols.S7;

/// <summary>S7 地址解析结果。地址格式: DB{number}.DB{type}{offset}，如 DB1.DBD0, DB10.DBW2</summary>
public sealed record S7Address
{
    /// <summary>数据块编号</summary>
    public int DbNumber { get; init; }

    /// <summary>数据类型: DBD (Double/DWord), DBW (Word), DBB (Byte), DBX (Bit)</summary>
    public string VarType { get; init; } = "DBD";

    /// <summary>字节偏移量</summary>
    public int ByteOffset { get; init; }

    /// <summary>位偏移（仅 DBX 有效）</summary>
    public int BitOffset { get; init; }
}
