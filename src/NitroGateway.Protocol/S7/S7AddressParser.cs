using System.Text.RegularExpressions;

namespace NitroGateway.Protocols.S7;

/// <summary>S7 地址解析器。DB1.DBD0 → {DbNumber:1, VarType:"DBD", ByteOffset:0}</summary>
public sealed partial class S7AddressParser
{
    [GeneratedRegex(@"^DB(\d+)\.DB([BDWX])(\d+)(?:\.(\d+))?$", RegexOptions.IgnoreCase)]
    private static partial Regex AddressRegex();

    /// <summary>解析 S7 地址字符串</summary>
    public static S7Address Parse(string address)
    {
        var match = AddressRegex().Match(address);
        if (!match.Success)
            throw new ArgumentException($"无效的 S7 地址: {address}");

        return new S7Address
        {
            DbNumber = int.Parse(match.Groups[1].Value),
            VarType = "DB" + match.Groups[2].Value.ToUpper(),  // DBD, DBW, DBB, DBX
            ByteOffset = int.Parse(match.Groups[3].Value),
            BitOffset = match.Groups[4].Success ? int.Parse(match.Groups[4].Value) : 0
        };
    }
}
