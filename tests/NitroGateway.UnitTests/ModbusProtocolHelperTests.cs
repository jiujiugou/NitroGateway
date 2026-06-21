using NitroGateway.Domain.Devices;
using NitroGateway.Protocol.Modbus;
using Xunit;

namespace NitroGateway.UnitTests;

public class ModbusProtocolHelperTests
{
    // ═══════════ DecodeRegisters ═══════════

    [Fact] public void Decode_Float_12_5() =>
        Assert.Equal(12.5, (double)ModbusProtocolHelper.DecodeRegisters([0x4148, 0x0000], DataType.Float), 2);

    [Fact] public void Decode_Float_Minus40() =>
        Assert.Equal(-40.0, (double)ModbusProtocolHelper.DecodeRegisters([0xC220, 0x0000], DataType.Float), 2);

    [Fact] public void Decode_Int16_Positive() =>
        Assert.Equal(42, Convert.ToInt16(ModbusProtocolHelper.DecodeRegisters([42], DataType.Int16)));

    [Fact] public void Decode_Int16_Negative() =>
        Assert.Equal(-100, Convert.ToInt16(ModbusProtocolHelper.DecodeRegisters([unchecked((ushort)-100)], DataType.Int16)));

    [Fact] public void Decode_UInt16() =>
        Assert.Equal((ushort)65535, ModbusProtocolHelper.DecodeRegisters([0xFFFF], DataType.UInt16));

    [Fact] public void Decode_Int32() =>
        Assert.Equal(100000, ModbusProtocolHelper.DecodeRegisters([0x0001, 0x86A0], DataType.Int32));

    [Fact] public void Decode_Bool_True() =>
        Assert.True((bool)ModbusProtocolHelper.DecodeRegisters([1], DataType.Bool));

    [Fact] public void Decode_Bool_False() =>
        Assert.False((bool)ModbusProtocolHelper.DecodeRegisters([0], DataType.Bool));

    [Fact] public void Decode_EmptyRegs_ReturnsZero() =>
        Assert.Equal(0, (int)ModbusProtocolHelper.DecodeRegisters([], DataType.Int16));

    [Fact] public void Decode_Double() =>
        Assert.Equal(3.14159, (double)ModbusProtocolHelper.DecodeRegisters([0x4009, 0x21FB, 0x5444, 0x2D18], DataType.Double), 4);

    // ═══════════ GroupByContinuity ═══════════

    [Fact]
    public void Group_SameAreaContiguous_Merged()
    {
        var points = new List<DevicePoint>
        {
            MakePoint("40001", DataType.Float),
            MakePoint("40003", DataType.Int16),  // 紧挨 Float (2regs)
            MakePoint("40004", DataType.Int32),
        };
        var groups = ModbusProtocolHelper.GroupByContinuity(points).ToList();
        Assert.Single(groups);
        Assert.Equal(3, groups[0].Count());
    }

    [Fact]
    public void Group_SameAreaNonContiguous_Split()
    {
        var points = new List<DevicePoint>
        {
            MakePoint("40001", DataType.Int16),
            MakePoint("40010", DataType.Int16),  // 间隔大
        };
        var groups = ModbusProtocolHelper.GroupByContinuity(points).ToList();
        Assert.Equal(2, groups.Count);
    }

    [Fact]
    public void Group_DifferentArea_Separate()
    {
        var points = new List<DevicePoint>
        {
            MakePoint("40001", DataType.Int16),
            MakePoint("30001", DataType.Int16),  // InputRegister
        };
        var groups = ModbusProtocolHelper.GroupByContinuity(points).ToList();
        Assert.Equal(2, groups.Count);
    }

    // ═══════════ EncodeValue ═══════════

    [Fact] public void Encode_Float_ToRegs() =>
        Assert.Equal(new ushort[] { 0x4148, 0x0000 }, ModbusProtocolHelper.EncodeValue(12.5f, DataType.Float));

    [Fact] public void Encode_Int16_ToRegs() =>
        Assert.Equal(new ushort[] { 42 }, ModbusProtocolHelper.EncodeValue(42, DataType.Int16));

    [Fact] public void Encode_Bool_True_ToRegs() =>
        Assert.Equal(new ushort[] { 1 }, ModbusProtocolHelper.EncodeValue(true, DataType.Bool));

    // ═══════════ ParseEndian ═══════════

    [Fact] public void Endian_Default() => Assert.Equal(ModbusEndian.ABCD, ModbusProtocolHelper.ParseEndian(null));

    [Fact] public void Endian_CDAB() => Assert.Equal(ModbusEndian.CDAB, ModbusProtocolHelper.ParseEndian("CDAB"));

    [Fact] public void Endian_DCBA() => Assert.Equal(ModbusEndian.DCBA, ModbusProtocolHelper.ParseEndian("dcba"));

    // helper
    private static DevicePoint MakePoint(string addr, DataType type) =>
        new() { Id = Guid.NewGuid(), Name = "test", Address = addr, DataType = type };
}
