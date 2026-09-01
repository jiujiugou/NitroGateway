namespace NitroGateway.Webapi.Models;

/// <summary>OPC UA 节点浏览结果 DTO（ADR-070 层次 1，前端树点选）。</summary>
public sealed class BrowseNodeDto
{
    /// <summary>节点地址（如 "ns=2;i=1001" / "ns=2;s=Tag"），可直接回填点位地址</summary>
    public string NodeId { get; init; } = "";

    /// <summary>显示名称</summary>
    public string Name { get; init; } = "";

    /// <summary>数据类型枚举名（"Int32"/"Float"/...；非变量节点为空串）</summary>
    public string TypeName { get; init; } = "";

    /// <summary>是否为变量节点（叶子，可回填点位）</summary>
    public bool IsVariable { get; init; }

    /// <summary>访问级别（"Read"/"ReadWrite"/"Write"/"None"；非变量节点为空串）</summary>
    public string Access { get; init; } = "";
}
