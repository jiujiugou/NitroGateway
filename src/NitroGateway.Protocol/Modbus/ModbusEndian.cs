namespace NitroGateway.Protocols.Modbus;

/// <summary>
/// Modbus 多寄存器字节序。
/// 当 DataType 占用多个寄存器（Float=2 个、Double=4 个）时使用。
/// </summary>
public enum ModbusEndian
{
    /// <summary>大端（默认）。寄存器 [0x41A0, 0x0000] → 12.5f</summary>
    ABCD,

    /// <summary>字交换（Word Swap），相邻寄存器对调</summary>
    CDAB,

    /// <summary>字节交换（Byte Swap），每个寄存器内高低字节交换</summary>
    BADC,

    /// <summary>小端，完全反转</summary>
    DCBA
}
