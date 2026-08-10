using System.Text.RegularExpressions;
using NitroGateway.Domain.Devices;

namespace NitroGateway.Protocols.S7;

/// <summary>
/// S7 地址解析器。支持 DB 区（DB1.DBD0 / DB1.DBX0.0）与 M/I/Q 区（M100、MW10、I0.0、Q0.2）。
/// DB → {DbNumber&gt;0, Area:"DB", VarType:"DBD"/"DBW"/"DBB"/"DBX"}；
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
                BitOffset = match.Groups[4].Success ? int.Parse(match.Groups[4].Value) : 0,
                HasBit = match.Groups[4].Success
            };
        }

        return new S7Address
        {
            DbNumber = 0,
            Area = match.Groups[5].Value.ToUpperInvariant(),               // M / I / Q
            VarType = match.Groups[6].Value.ToUpperInvariant(),            // D / W / B，位地址为空串
            ByteOffset = int.Parse(match.Groups[7].Value),
            BitOffset = match.Groups[8].Success ? int.Parse(match.Groups[8].Value) : 0,
            HasBit = match.Groups[8].Success
        };
    }

    /// <summary>
    /// 将地址按点位 DataType 校验并格式化为 Hsl 驱动地址（ADR-024 P1-3）。
    /// 规则：地址自带类型优先（与 DB 区一致，DBB/B ↔ Byte/String，DBW/W ↔ Int16/UInt16，
    /// DBD/D ↔ 32/64 位数值与 Float/Double，位地址仅 Bool）；M/I/Q 区无类型后缀时按 DataType 推导；
    /// 位偏移仅允许 Bool，类型冲突抛 ArgumentException（宁可显式失败，不静默读错字节长度）。
    /// </summary>
    public static string FormatForHsl(string address, DataType dataType)
    {
        var a = Parse(address);

        if (a.DbNumber > 0)
        {
            if (a.VarType == "DBX")
            {
                Require(dataType == DataType.Bool, address, a.VarType, dataType, "位地址仅支持 Bool 点位");
                return $"DB{a.DbNumber}.DBX{a.ByteOffset}.{a.BitOffset}";
            }

            if (a.HasBit)
                throw new ArgumentException($"无效的 S7 地址: {address}（位偏移仅允许用于位地址 DBX）");

            Require(IsCompatible(a.VarType, dataType), address, a.VarType, dataType, null);
            return $"DB{a.DbNumber}.{a.VarType}{a.ByteOffset}";
        }

        // M/I/Q 区：位地址（含无后缀 Bool）固定输出 {Area}{offset}.{bit}
        if (a.HasBit || dataType == DataType.Bool)
        {
            Require(dataType == DataType.Bool, address, a.VarType, dataType, "位地址仅支持 Bool 点位");
            Require(a.BitOffset is >= 0 and <= 7, address, a.VarType, dataType, "位偏移需在 0-7 之间");
            return $"{a.Area}{a.ByteOffset}.{a.BitOffset}";
        }

        // 地址自带类型优先；无类型后缀按 DataType 推导
        var type = a.VarType.Length > 0 ? a.VarType : DeriveType(dataType);
        Require(IsCompatible(type, dataType), address, type, dataType, null);
        return $"{a.Area}{type}{a.ByteOffset}";
    }

    /// <summary>判断地址是否为位地址（DBX 或带位后缀），用于 Ping 等按位/字选择读法（ADR-024 P2-2）</summary>
    public static bool IsBitAddress(string address)
    {
        var a = Parse(address);
        return a.VarType == "DBX" || a.HasBit;
    }

    private static bool IsCompatible(string type, DataType dataType) => type switch
    {
        "DBB" or "B" => dataType is DataType.Byte or DataType.String,
        "DBW" or "W" => dataType is DataType.Int16 or DataType.UInt16,
        "DBD" or "D" => dataType is DataType.Int32 or DataType.UInt32 or DataType.Int64 or DataType.UInt64 or DataType.Float or DataType.Double,
        "DBX" => dataType == DataType.Bool,
        _ => false
    };

    private static string DeriveType(DataType dataType) => dataType switch
    {
        DataType.Bool => "",
        DataType.Byte or DataType.String => "B",
        DataType.Int16 or DataType.UInt16 => "W",
        _ => "D"
    };

    private static void Require(bool ok, string address, string varType, DataType dataType, string? extra)
    {
        if (ok) return;
        var detail = extra ?? $"地址类型 {varType} 与点位类型 {dataType} 不兼容";
        throw new ArgumentException($"无效的 S7 地址: {address}（{detail}）");
    }
}
