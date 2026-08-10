using NitroGateway.Domain.Devices;
using NitroGateway.Protocols.S7;
using Xunit;

namespace NitroGateway.UnitTests;

/// <summary>S7 地址解析器测试（ADR-019 P2-3）：DB 区与 M/I/Q 区。</summary>
public class S7AddressParserTests
{
    [Theory]
    // DB 区
    [InlineData("DB1.DBD0", 1, "DB", "DBD", 0, 0)]
    [InlineData("DB10.DBW2", 10, "DB", "DBW", 2, 0)]
    [InlineData("DB3.DBB4", 3, "DB", "DBB", 4, 0)]
    [InlineData("DB2.DBX0.3", 2, "DB", "DBX", 0, 3)]
    // M/I/Q 区
    [InlineData("M100", 0, "M", "", 100, 0)]
    [InlineData("MW10", 0, "M", "W", 10, 0)]
    [InlineData("MD20", 0, "M", "D", 20, 0)]
    [InlineData("I0.0", 0, "I", "", 0, 0)]
    [InlineData("Q0.2", 0, "Q", "", 0, 2)]
    public void Parse_SupportedFormats(string address, int db, string area, string varType, int offset, int bit)
    {
        var a = S7AddressParser.Parse(address);
        Assert.Equal(db, a.DbNumber);
        Assert.Equal(area, a.Area);
        Assert.Equal(varType, a.VarType);
        Assert.Equal(offset, a.ByteOffset);
        Assert.Equal(bit, a.BitOffset);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ABC")]
    [InlineData("DB1.DBX")]
    [InlineData("M")]
    [InlineData("192.168.1.1:502")]
    public void Parse_InvalidFormat_Throws(string address)
    {
        Assert.Throws<ArgumentException>(() => S7AddressParser.Parse(address));
    }

    // ══════════════════════════════════════════════════
    //  FormatForHsl（ADR-024 P1-3：地址自带类型优先 + 类型冲突显式报错）
    // ══════════════════════════════════════════════════

    [Theory]
    // DB 区：类型与 DataType 匹配时原样输出
    [InlineData("DB1.DBD0", DataType.Float, "DB1.DBD0")]
    [InlineData("DB1.DBD0", DataType.Int32, "DB1.DBD0")]
    [InlineData("DB1.DBD0", DataType.Double, "DB1.DBD0")]
    [InlineData("DB10.DBW2", DataType.Int16, "DB10.DBW2")]
    [InlineData("DB10.DBW2", DataType.UInt16, "DB10.DBW2")]
    [InlineData("DB3.DBB4", DataType.Byte, "DB3.DBB4")]
    [InlineData("DB3.DBB4", DataType.String, "DB3.DBB4")]
    [InlineData("DB2.DBX0.3", DataType.Bool, "DB2.DBX0.3")]
    [InlineData("DB1.DBX0", DataType.Bool, "DB1.DBX0.0")]
    // M/I/Q 区：地址自带类型优先（MW10+Int16 保持字地址，不再按 Float 推导成 MD10）
    [InlineData("MW10", DataType.Int16, "MW10")]
    [InlineData("MD20", DataType.Float, "MD20")]
    [InlineData("MB5", DataType.Byte, "MB5")]
    [InlineData("M100.2", DataType.Bool, "M100.2")]
    [InlineData("I0.0", DataType.Bool, "I0.0")]
    [InlineData("Q0.2", DataType.Bool, "Q0.2")]
    // M/I/Q 区：无类型后缀时按 DataType 推导
    [InlineData("M100", DataType.Float, "MD100")]
    [InlineData("M100", DataType.Int16, "MW100")]
    [InlineData("M100", DataType.Byte, "MB100")]
    [InlineData("M100", DataType.Bool, "M100.0")]
    [InlineData("I0", DataType.Bool, "I0.0")]
    public void FormatForHsl_CompatibleType_Formats(string address, DataType type, string expected)
    {
        Assert.Equal(expected, S7AddressParser.FormatForHsl(address, type));
    }

    [Theory]
    // 地址自带类型与 DataType 冲突 → 显式报错而非静默读错字节长度
    [InlineData("MW10", DataType.Float)]
    [InlineData("MD20", DataType.Int16)]
    [InlineData("MB5", DataType.Int32)]
    [InlineData("DB1.DBB0", DataType.Int16)]
    [InlineData("DB1.DBW0", DataType.Float)]
    [InlineData("DB1.DBD0", DataType.Byte)]
    [InlineData("DB1.DBX0.0", DataType.Float)]
    // 非位类型携带位后缀 → 报错而非静默丢弃
    [InlineData("DB1.DBD0.5", DataType.Float)]
    [InlineData("DB1.DBW0.1", DataType.Int16)]
    [InlineData("M100.2", DataType.Float)]
    [InlineData("MB100.2", DataType.Byte)]
    public void FormatForHsl_TypeConflict_Throws(string address, DataType type)
    {
        Assert.Throws<ArgumentException>(() => S7AddressParser.FormatForHsl(address, type));
    }

    [Theory]
    [InlineData("DB1.DBX0.3", true)]
    [InlineData("DB1.DBX0", true)]
    [InlineData("M100.2", true)]
    [InlineData("MW10", false)]
    [InlineData("DB1.DBD0", false)]
    [InlineData("I0.0", true)]
    public void IsBitAddress_DetectsBitAddresses(string address, bool expected)
    {
        Assert.Equal(expected, S7AddressParser.IsBitAddress(address));
    }
}




