using NitroGateway.Domain.Protocols;
using NitroGateway.Shared;

namespace NitroGateway.Desktop.Services.Connectivity;

/// <summary>
/// OPC UA 节点浏览抽象（ADR-070 层次 1）。与 Webapi 的 <c>OpcUaBrowseController</c> 同语义，
/// 进程内复用 <see cref="NitroGateway.Protocols.IProtocolDriverPool"/> 的长连接驱动：
/// 未连接先建连、用后不断连；浏览失败/超时不置 Faulted（不污染采集状态机）。
/// </summary>
public interface IOpcUaNodeBrowser
{
    /// <summary>浏览指定节点下的子节点（单层）。<paramref name="parentNodeId"/> 为空 = 根目录（Objects）。</summary>
    Task<OperationResult<IReadOnlyList<BrowseNode>>> BrowseAsync(
        Guid deviceId, string parentNodeId, CancellationToken ct = default);
}
