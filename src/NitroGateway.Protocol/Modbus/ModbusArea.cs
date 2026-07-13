namespace NitroGateway.Protocols.Modbus;

/// <summary>Modbus 数据功能区</summary>
public enum ModbusArea
{
    /// <summary>线圈（0xxxx），FC1 读 / FC5 写</summary>
    Coil,

    /// <summary>离散输入（1xxxx），FC2 读</summary>
    DiscreteInput,

    /// <summary>输入寄存器（3xxxx），FC4 读</summary>
    InputRegister,

    /// <summary>保持寄存器（4xxxx），FC3 读 / FC6/FC16 写</summary>
    HoldingRegister
}
