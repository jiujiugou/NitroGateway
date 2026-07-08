namespace NitroGateway.Domain.Devices;

/// <summary>DataType 扩展方法</summary>
public static class DataTypeExtensions
{
    /// <summary>
    /// 获取数据类型占用的 Modbus 寄存器数量。
    /// Bool=1, Byte=1, Int16=1, UInt16=1, Int32=2, UInt32=2,
    /// Int64=4, UInt64=4, Float=2, Double=4, String=自定义(按2估算)
    /// </summary>
    public static int RegisterCount(this DataType type) => type switch
    {
        DataType.Bool   => 1,
        DataType.Byte   => 1,
        DataType.Int16  => 1,
        DataType.UInt16 => 1,
        DataType.Int32  => 2,
        DataType.UInt32 => 2,
        DataType.Int64  => 4,
        DataType.UInt64 => 4,
        DataType.Float  => 2,
        DataType.Double => 4,
        DataType.String => 2,  // 至少 2 个寄存器
        _ => 1
    };
}
