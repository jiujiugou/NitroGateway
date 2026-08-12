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
}
