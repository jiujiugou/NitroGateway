namespace NitroGateway.Webapi.Models;

/// <summary>站点改名/绑定显示名请求（ADR-036 中心站点管理）</summary>
public sealed class RenameSiteRequest
{
    /// <summary>可读显示名；空串 = 清除绑定（回到"未命名"），最长 100 字符。</summary>
    public string DisplayName { get; init; } = "";
}
