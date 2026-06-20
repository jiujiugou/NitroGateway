using NitroGateway.Domain.Protocols;

namespace NitroGateway.Protocol.OpcUa;

/// <summary>OPC UA 驱动能力</summary>
public static class OpcUaDriverCapability
{
    /// <summary>OPC UA 能力：批量读写 + 订阅 + 不限数量</summary>
    public static readonly DriverCapability Instance = new()
    {
        SupportsBatchRead = true,
        SupportsBatchWrite = true,
        SupportsSubscription = true,
        MaxBatchSize = 0   // 无限制
    };
}
