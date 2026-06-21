using NitroGateway.Protocol.Modbus;
using Xunit;

namespace NitroGateway.UnitTests;

public class ModbusAddressParserTests
{
    private readonly ModbusAddressParser _parser = new();

    [Fact]
    public void Parse_40001_HoldingRegister_Offset0()
    {
        var addr = (ModbusAddress)_parser.Parse("40001");
        Assert.Equal(ModbusArea.HoldingRegister, addr.Area);
        Assert.Equal(0, (int)addr.Offset);
    }

    [Fact]
    public void Parse_30001_InputRegister()
    {
        var addr = (ModbusAddress)_parser.Parse("30001");
        Assert.Equal(ModbusArea.InputRegister, addr.Area);
    }

    [Fact]
    public void Parse_00001_Coil()
    {
        var addr = (ModbusAddress)_parser.Parse("00001");
        Assert.Equal(ModbusArea.Coil, addr.Area);
    }

    [Fact]
    public void Parse_10001_DiscreteInput()
    {
        var addr = (ModbusAddress)_parser.Parse("10001");
        Assert.Equal(ModbusArea.DiscreteInput, addr.Area);
    }

    [Fact]
    public void Parse_40001_ZeroBased()
    {
        var addr = (ModbusAddress)_parser.Parse("40001");
        Assert.Equal(0, (int)addr.Offset);  // PLC 式: 40001=偏移0
    }

    [Fact]
    public void ParseWithCount_Float_2Regs()
    {
        var addr = _parser.ParseWithCount("40001", Domain.Devices.DataType.Float);
        Assert.Equal(2, (int)addr.Count);
    }

    [Fact]
    public void ParseWithCount_Int16_1Reg()
    {
        var addr = _parser.ParseWithCount("40001", Domain.Devices.DataType.Int16);
        Assert.Equal(1, (int)addr.Count);
    }

    [Fact]
    public void GetDistance_Adjacent_0()
    {
        var a = (ModbusAddress)_parser.ParseWithCount("40001", Domain.Devices.DataType.Int16);
        var b = (ModbusAddress)_parser.ParseWithCount("40002", Domain.Devices.DataType.Int16);
        Assert.Equal(0, _parser.GetDistance(a, b));  // 紧邻
    }

    [Fact]
    public void GetDistance_DifferentArea_Negative()
    {
        var a = (ModbusAddress)_parser.Parse("40001");
        var b = (ModbusAddress)_parser.Parse("30001");
        Assert.Equal(-1, _parser.GetDistance(a, b));
    }
}
