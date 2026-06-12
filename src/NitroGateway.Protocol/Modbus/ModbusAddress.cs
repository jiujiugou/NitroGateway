namespace NitroGateway.Protocol.Modbus;

/// <summary>Modbus 地址</summary>
public sealed record ModbusAddress(
    ModbusArea Area,
    ushort Offset,
    ushort Count)
    : PointAddress;
