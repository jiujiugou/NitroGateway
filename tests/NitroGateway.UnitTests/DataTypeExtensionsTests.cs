using NitroGateway.Domain.Devices;
using Xunit;

namespace NitroGateway.UnitTests;

/// <summary>
/// DataType.RegisterCount() 测试——地址递增和批量生成的基础。
/// 一处值写错，CSV 批量生成和 Modbus 模板的地址计算全偏移。
/// </summary>
public class DataTypeExtensionsTests
{
    [Fact] public void Bool_OneRegister()    => Assert.Equal(1, DataType.Bool.RegisterCount());
    [Fact] public void Byte_OneRegister()    => Assert.Equal(1, DataType.Byte.RegisterCount());
    [Fact] public void Int16_OneRegister()   => Assert.Equal(1, DataType.Int16.RegisterCount());
    [Fact] public void UInt16_OneRegister()  => Assert.Equal(1, DataType.UInt16.RegisterCount());
    [Fact] public void Int32_TwoRegisters()  => Assert.Equal(2, DataType.Int32.RegisterCount());
    [Fact] public void UInt32_TwoRegisters() => Assert.Equal(2, DataType.UInt32.RegisterCount());
    [Fact] public void Float_TwoRegisters()  => Assert.Equal(2, DataType.Float.RegisterCount());
    [Fact] public void Double_FourRegisters() => Assert.Equal(4, DataType.Double.RegisterCount());
    [Fact] public void Int64_FourRegisters() => Assert.Equal(4, DataType.Int64.RegisterCount());
    [Fact] public void String_TwoRegisters() => Assert.Equal(2, DataType.String.RegisterCount());
}
