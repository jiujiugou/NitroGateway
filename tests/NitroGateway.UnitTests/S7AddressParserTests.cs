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
    [InlineData("DB1.DBD0", 1, "DB", "DBD", 0, 0)]
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
}
