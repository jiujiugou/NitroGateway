using NitroGateway.Domain.Devices;
using NitroGateway.Shared;

namespace NitroGateway.Collection;

/// <summary>数据分发：写时序库 + 入转发缓冲，双写独立</summary>
public interface IDataDispatcher
{
    Task<OperationResult> DispatchAsync(
        Guid deviceId, IReadOnlyList<PointSnapshot> snapshots, CancellationToken ct);
}
