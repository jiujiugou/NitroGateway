namespace NitroGateway.Protocols.S7;

/// <summary>
/// S7 地址解析结果。支持 DB 区（DB1.DBD0）与 M/I/Q 区（M100、I0.0）。
/// </summary>
public sealed record S7Address
{
    /// <summary>数据块编号；非 DB 区为 0</summary>
    public int DbNumber { get; init; }

    /// <summary>存储区：DB / M / I / Q</summary>
    public string Area { get; init; } = "DB";

    /// <summary>DB 区为 DBD/DBW/DBB/DBX；M/I/Q 区为可选类型字符 D/W/B（位地址为空串）</summary>
    public string VarType { get; init; } = "DBD";

    /// <summary>字节偏移量</summary>
    public int ByteOffset { get; init; }

    /// <summary>位偏移（仅位地址有效，如 DBX0.3 / M100.2）</summary>
    public int BitOffset { get; init; }

    /// <summary>地址串是否带位后缀（如 DBX0.3、M100.2）；用于校验非位类型不得携带位偏移（ADR-024 P1-3）</summary>
    public bool HasBit { get; init; }
}
