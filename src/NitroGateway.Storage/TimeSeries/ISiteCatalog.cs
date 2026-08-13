using NitroGateway.Shared;

namespace NitroGateway.Storage.TimeSeries;

/// <summary>
/// 站点目录接口（ADR-035 第 1 步 Web 维度）。返回中心库中实际出现过数据的 siteId 列表，
/// 供 Web 管理面板按站点过滤（设备列表无站点属性，站点是 measurement/alarm 的数据维度）。
/// 站点来源 = measurements 与 alarms 两表 site_id 去重合并；空串（未标注站点）不列入。
/// </summary>
public interface ISiteCatalog
{
    /// <summary>
    /// 获取全部已标注站点（去重、按字典序排序）。
    /// 查询失败按 OperationResult 归类返回，不抛出。
    /// </summary>
    Task<OperationResult<IReadOnlyList<string>>> GetSitesAsync(CancellationToken ct = default);

    /// <summary>
    /// 注册站点（ADR-036）：首见插入，后续更新 last_seen；site_id 唯一索引兜底，
    /// 同一站点被多台机器上报时保留首见来源指纹（source_client_id）供冲突检测。
    /// 注册失败不阻断数据入库（由调用方降级为仅记录日志）。
    /// </summary>
    Task<OperationResult> RegisterSiteAsync(string siteId, string? sourceClientId, CancellationToken ct = default);

    /// <summary>
    /// 获取站点详情列表（ADR-036 中心站点管理）：含显示名、来源指纹与冲突标记；
    /// 未注册（仅历史数据）站点一并返回（display_name 为空、无指纹）。
    /// </summary>
    Task<OperationResult<IReadOnlyList<SiteInfo>>> GetSiteInfosAsync(CancellationToken ct = default);

    /// <summary>
    /// 重命名/绑定站点显示名（ADR-036）：upsert 到 sites 表（未注册站点一并建档）。
    /// </summary>
    Task<OperationResult> RenameSiteAsync(string siteId, string displayName, CancellationToken ct = default);
}
