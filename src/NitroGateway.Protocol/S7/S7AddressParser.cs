using System.Text.RegularExpressions;

namespace NitroGateway.Protocols.S7;

/// <summary>
/// S7 地址解析器。支持 DB 区（DB1.DBD0 / DB1.DBX0.0）与 M/I/Q 区（M100、MW10、I0.0、Q0.2）。
/// DB → {DbNumber>0, Area:"DB", VarType:"DBD"/"DBW"/"DBB"/"DBX"}；
/// M/I/Q → {DbNumber:0, Area:"M"/"I"/"Q", VarType:可选类型字符 D/W/B（位地址为空串）, ByteOffset, BitOffset}。
/// </summary>
public sealed partial class S7AddressParser
{
    [GeneratedRegex(
        @"^(?:DB(\d+)\.DB([BDWX])(\d+)(?:\.(\d+))?|([MIQ])([BDW]?)(\d+)(?:\.(\d+))?)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex AddressRegex();

    /// <summary>解析 S7 地址字符串；非法格式抛 ArgumentException</summary>
    public static S7Address Parse(string address)
    {
        var match = AddressRegex().Match(address);
        if (!match.Success)
            throw new ArgumentException($"无效的 S7 地址: {address}");

        if (match.Groups[1].Success)
        {
            return new S7Address
            {
                DbNumber = int.Parse(match.Groups[1].Value),
                Area = "DB",
                VarType = "DB" + match.Groups[2].Value.ToUpperInvariant(),  // DBD, DBW, DBB, DBX
                ByteOffset = int.Parse(match.Groups[3].Value),
                BitOffset = match.Groups[4].Success ? int.Parse(match.Groups[4].Value) : 0
            };
        }

        return new S7Address
        {
            DbNumber = 0,
            Area = match.Groups[5].Value.ToUpperInvariant(),               // M / I / Q
            VarType = match.Groups[6].Value.ToUpperInvariant(),            // D / W / B，位地址为空串
            ByteOffset = int.Parse(match.Groups[7].Value),
            BitOffset = match.Groups[8].Success ? int.Parse(match.Groups[8].Value) : 0
        };
    }
}
