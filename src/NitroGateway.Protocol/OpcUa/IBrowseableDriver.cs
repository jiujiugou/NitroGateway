using NitroGateway.Shared;

namespace NitroGateway.Protocol.OpcUa;

/// <summary>
/// 节点 Browse 能力。和 IProtocolDriver 分离——采集引擎不调这个，配置/导入工具调。
/// </summary>
public interface IBrowseableDriver
{
    /// <summary>浏览指定节点下的子节点</summary>
    Task<OperationResult<IReadOnlyList<BrowseNode>>> BrowseAsync(string parentNodeId = "", CancellationToken ct = default);
}

/// <summary>Browse 返回的节点信息</summary>
public sealed record BrowseNode
{
    public required string NodeId { get; init; }
    public required string Name { get; init; }
    public required string TypeName { get; init; }
    public required bool IsVariable { get; init; }
    public required string Access { get; init; }
}
