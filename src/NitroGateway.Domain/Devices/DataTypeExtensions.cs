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

    /// <summary>
    /// 获取数据类型占用的 S7 字节宽度，用于 DB 区批量生成地址递增（ADR-024 P3-3）。
    /// Byte=1, Int16/UInt16=2, Int32/UInt32/Float=4, Int64/UInt64/Double=8, String=10（与驱动默认字符串长度对齐）；
    /// Bool 位地址不支持批量生成（位步进需按字节内偏移处理，批量场景易错，见 PointBatchService）。
    /// </summary>
    public static int ByteSize(this DataType type) => type switch
    {
        DataType.Bool   => 1,
        DataType.Byte   => 1,
        DataType.Int16  => 2,
        DataType.UInt16 => 2,
        DataType.Int32  => 4,
        DataType.UInt32 => 4,
        DataType.Int64  => 8,
        DataType.UInt64 => 8,
        DataType.Float  => 4,
        DataType.Double => 8,
        DataType.String => 10,
        _ => 4
    };
}
