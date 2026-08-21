using NitroGateway.Protocols.OpcUa;
using NitroGateway.Protocols;
using Xunit;

namespace NitroGateway.UnitTests;

/// <summary>
/// OPC UA 地址解析器测试（12-OPC-UA接入设计.md S5）：
/// NodeId 四型标识符（s=String / i=Numeric / g=Guid / b=Opaque）解析 + 非法地址抛 ArgumentException + 序列化往返一致。
/// </summary>
public class OpcUaAddressParserTests
{
    private readonly OpcUaAddressParser _parser = new();

    [Fact]
    public void Parse_StringId_Namespace3()
    {
        var addr = (OpcUaAddress)_parser.Parse("ns=3;s=Temperature");
        Assert.Equal((ushort)3, addr.NamespaceIndex);
        Assert.Equal("Temperature", addr.StringId);
        Assert.Null(addr.NumericId);
    }

    [Fact]
    public void Parse_NumericId_Namespace2()
    {
        var addr = (OpcUaAddress)_parser.Parse("ns=2;i=1001");
        Assert.Equal((ushort)2, addr.NamespaceIndex);
        Assert.Equal(1001u, addr.NumericId);
    }

    [Fact]
    public void Parse_GuidId_Namespace4()
    {
        var guid = Guid.Parse("8f6d7f3e-2c4a-4b1d-9e0c-1234567890ab");
        var addr = (OpcUaAddress)_parser.Parse($"ns=4;g={guid}");
        Assert.Equal((ushort)4, addr.NamespaceIndex);
        Assert.Equal(guid, addr.GuidId);
    }

    [Fact]
    public void Parse_OpaqueId_Namespace5_Base64()
    {
        var addr = (OpcUaAddress)_parser.Parse("ns=5;b=AQIDBA==");
        Assert.Equal((ushort)5, addr.NamespaceIndex);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, addr.OpaqueId);
    }

    /// <summary>序列化往返：解析 → 序列化 → 原始地址字符串一致（四型全覆盖）</summary>
    [Theory]
    [InlineData("ns=3;s=Temperature")]
    [InlineData("ns=2;i=1001")]
    [InlineData("ns=0;s=Server")]
    [InlineData("ns=4;g=8f6d7f3e-2c4a-4b1d-9e0c-1234567890ab")]
    [InlineData("ns=5;b=AQIDBA==")]
    public void Serialize_RoundTrip_RawEqual(string raw)
    {
        var addr = _parser.Parse(raw);
        Assert.Equal(raw, _parser.Serialize(addr));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_Empty_Throws(string raw)
    {
        Assert.Throws<ArgumentException>(() => _parser.Parse(raw));
    }

    /// <summary>缺 ';' 分隔（无标识符段）→ 抛异常</summary>
    [Fact]
    public void Parse_MissingSeparator_Throws()
    {
        Assert.Throws<ArgumentException>(() => _parser.Parse("ns=3"));
    }

    /// <summary>NamespaceIndex 非法 → 抛异常</summary>
    [Fact]
    public void Parse_InvalidNamespace_Throws()
    {
        Assert.Throws<ArgumentException>(() => _parser.Parse("ns=abc;s=foo"));
    }

    /// <summary>未知标识符类型（非 s/i/g/b）→ 抛异常</summary>
    [Fact]
    public void Parse_UnknownIdType_Throws()
    {
        Assert.Throws<ArgumentException>(() => _parser.Parse("ns=3;x=foo"));
    }

    /// <summary>NumericId 非法 → 抛异常</summary>
    [Fact]
    public void Parse_InvalidNumericId_Throws()
    {
        Assert.Throws<ArgumentException>(() => _parser.Parse("ns=2;i=not-a-number"));
    }

    /// <summary>非 OPC UA 地址类型传入 Serialize → 抛异常</summary>
    [Fact]
    public void Serialize_WrongAddressType_Throws()
    {
        Assert.Throws<ArgumentException>(() => _parser.Serialize(new DummyAddress { Raw = "dummy" }));
    }

    /// <summary>地址缺标识符值（s= 后为空）→ 序列化时抛异常（无标识符不可表达）</summary>
    [Fact]
    public void Serialize_MissingIdentifier_Throws()
    {
        var addr = new OpcUaAddress { Raw = "ns=3;s=", NamespaceIndex = 3 };
        Assert.Throws<ArgumentException>(() => _parser.Serialize(addr));
    }

    private sealed record DummyAddress : PointAddress;
}
