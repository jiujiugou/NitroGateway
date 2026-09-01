using NitroGateway.Shared;

namespace NitroGateway.Domain.Protocols;

/// <summary>
/// 节点 Browse 能力（ADR-070 层次 1 P0-1）。和 <see cref="IProtocolDriver"/> 分类——
/// 采集引擎不调这个，配置/导入工具（Webapi 浏览 API → 前端点选）调。
/// </summary>
/// <remarks>
/// 放 Domain.Protocols（与 <see cref="IProtocolDriver"/> 同级）：驱动池返回的是
/// <see cref="NitroGateway.Protocols.ProtocolDriverPool"/> 装饰器实例，浏览必须经装饰器
/// 转发到具体驱动才能复用长连接；接口下沉到 Domain 后装饰器与具体驱动（OPC UA）都能实现。
/// </remarks>
public interface IBrowseableDriver
{
    /// <summary>浏览指定节点下的子节点（单层，非递归；parent 缺省 = 根目录/Objects）</summary>
    Task<OperationResult<IReadOnlyList<BrowseNode>>> BrowseAsync(string parentNodeId = "", CancellationToken ct = default);
}

/// <summary>Browse 返回的节点信息（NodeId 与地址解析器序列化格式一致，可直接回填点位地址）</summary>
public sealed record BrowseNode
{
    /// <summary>节点地址（如 "ns=2;i=1001" / "ns=2;s=Tag"），与 OpcUaAddressParser 格式一致</summary>
    public required string NodeId { get; init; }

    /// <summary>显示名称</summary>
    public required string Name { get; init; }

    /// <summary>数据类型枚举名（"Int32"/"Float"/...；非变量节点为空串）</summary>
    public required string TypeName { get; init; }

    /// <summary>是否为变量节点（叶子，可回填点位）</summary>
    public required bool IsVariable { get; init; }

    /// <summary>访问级别（"Read"/"ReadWrite"/"Write"/"None"；非变量节点为空串）</summary>
    public required string Access { get; init; }
}
