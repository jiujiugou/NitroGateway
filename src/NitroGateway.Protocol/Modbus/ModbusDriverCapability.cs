using NitroGateway.Domain.Protocols;

namespace NitroGateway.Protocol.Modbus;

/// <summary>Modbus 驱动能力</summary>
public static class ModbusDriverCapability
{
    /// <summary>Modbus 默认能力：支持批量读写，单次最多 125 个寄存器，不支持订阅</summary>
    public static readonly DriverCapability Instance = new()
    {
        SupportsBatchRead = true,
        SupportsBatchWrite = true,
        SupportsSubscription = false,
        MaxBatchSize = 125
    };
}
