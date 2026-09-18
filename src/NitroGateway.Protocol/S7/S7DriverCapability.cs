using NitroGateway.Domain.Protocols;

namespace NitroGateway.Protocols.S7;

public static class S7DriverCapability
{
    public static readonly DriverCapability Instance = new()
    {
        SupportsBatchRead = false,
        SupportsBatchWrite = false,
        SupportsSubscription = false,
        MaxBatchSize = 1
    };
}
