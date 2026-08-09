using NitroGateway.Domain.Protocols;

namespace NitroGateway.Protocols.S7;

/// <summary>
/// S7 驱动能力。ReadBatchAsync/WriteBatchAsync 当前为逐点串行聚合（未实现真实批量读写），
/// 能力声明下调为不支持批量，避免"声称支持但实现是逐点"的误导（ADR-019 P3-4）；
/// 上层统一走 ReadBatchAsync/WriteBatchAsync 入口即可，逐点聚合对外行为不变。
/// </summary>
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
