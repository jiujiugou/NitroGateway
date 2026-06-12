namespace NitroGateway.Domain.Devices;

/// <summary>点位数据类型，覆盖工业协议常见标量类型</summary>
public enum DataType
{
    /// <summary>布尔型（1 bit）</summary>
    Bool,

    /// <summary>无符号 8 位整数</summary>
    Byte,

    /// <summary>有符号 16 位整数</summary>
    Int16,

    /// <summary>无符号 16 位整数</summary>
    UInt16,

    /// <summary>有符号 32 位整数</summary>
    Int32,

    /// <summary>无符号 32 位整数</summary>
    UInt32,

    /// <summary>有符号 64 位整数</summary>
    Int64,

    /// <summary>无符号 64 位整数</summary>
    UInt64,

    /// <summary>32 位 IEEE 浮点数</summary>
    Float,

    /// <summary>64 位 IEEE 浮点数</summary>
    Double,

    /// <summary>字符串</summary>
    String
}
