namespace NitroGateway.Shared;

/// <summary>
/// 站点标识（siteId）配置解析（ADR-035 第 1 步）。
/// 站点标识随上行数据流契约使用：MQTT topic 第三层 <c>nitrogateway/{siteId}/{deviceId}/…</c>、
/// BatchMeasurements 负载与中心库 site_id 列。
/// 配置键 <c>Site:Id</c>；缺省 "default"（单现场/一体机部署无需显式配置）。
/// </summary>
public static class SiteOptions
{
    /// <summary>配置节名：Site</summary>
    public const string SectionName = "Site";

    /// <summary>配置键：Site:Id</summary>
    public const string IdKey = "Site:Id";

    /// <summary>缺省站点标识：单现场部署不配置时使用</summary>
    public const string DefaultSiteId = "default";

    /// <summary>
    /// 解析站点标识：取 <c>Site:Id</c> 配置值并去除首尾空白；缺失/空白回退 <see cref="DefaultSiteId"/>。
    /// 保证上行 topic 与负载永远带非空 siteId，避免产生 <c>nitrogateway//device/…</c> 的坏 topic。
    /// </summary>
    public static string Resolve(string? configuredValue)
    {
        var id = configuredValue?.Trim();
        return string.IsNullOrEmpty(id) ? DefaultSiteId : id;
    }
}
