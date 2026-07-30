using NitroGateway.Domain.Protocols;

namespace NitroGateway.Protocols.Mitsubishi;

public static class MitsubishiDriverCapability
{
    public static readonly DriverCapability Instance = new()
    {
        SupportsBatchRead = false, SupportsBatchWrite = false,
        SupportsSubscription = false, MaxBatchSize = 0
    };
}
