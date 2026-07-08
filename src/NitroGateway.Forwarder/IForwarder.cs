using NitroGateway.Shared;

namespace NitroGateway.Forwarder;

/// <summary>数据转发：Dequeue → Serialize → Send → Commit</summary>
public interface IForwarder
{
    /// <summary>处理一批待转发数据</summary>
    Task<OperationResult> ForwardBatchAsync(int maxCount, CancellationToken ct = default);
}
