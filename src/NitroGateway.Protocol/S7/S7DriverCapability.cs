using NitroGateway.Domain.Protocols;

namespace NitroGateway.Protocols.S7;

/// <summary>S7 驱动能力：支持批量读写，单次最多 20 个点位，不支持订阅</summary>
public static class S7DriverCapability
{
    public static readonly DriverCapability Instance = new()
    {
        SupportsBatchRead = true,
        SupportsBatchWrite = true,
        SupportsSubscription = false,
        MaxBatchSize = 20
    };
}
