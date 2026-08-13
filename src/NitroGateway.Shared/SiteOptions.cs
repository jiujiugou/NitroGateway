using System.Security.Cryptography;
using System.Text.RegularExpressions;

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

    /// <summary>siteId 最大长度（MQTT topic 段与 URL 友好）</summary>
    public const int SiteIdMaxLength = 32;

    /// <summary>siteId 格式：小写字母/数字开头，后续可含连字符；禁止 / + # 空格等 topic 分隔/通配符</summary>
    public const string SiteIdPattern = "^[a-z0-9][a-z0-9-]{0,31}$";

    /// <summary>自动生成字符集：小写 base32（去除易混淆 i/l/o/u）</summary>
    private const string SiteIdAlphabet = "0123456789abcdefghjkmnpqrstvwxyz";

    /// <summary>自动生成随机段长度（40 位熵，万级现场碰撞可忽略；中心 sites 唯一索引兜底）</summary>
    private const int SiteIdRandomLength = 10;

    /// <summary>
    /// siteId 合法性：匹配 <see cref="SiteIdPattern"/> 且非保留值 <see cref="DefaultSiteId"/>。
    /// "default" 是"未初始化"哨兵（旧版缺省），正式站点禁止使用（ADR-036）。
    /// </summary>
    public static bool IsValidSiteId(string? siteId) =>
        !string.IsNullOrEmpty(siteId)
        && !string.Equals(siteId, DefaultSiteId, StringComparison.Ordinal)
        && Regex.IsMatch(siteId, SiteIdPattern, RegexOptions.CultureInvariant);

    /// <summary>
    /// 自动生成唯一站点标识（ADR-036）：<c>site-</c> + 10 位随机（加密随机源）。
    /// 概率唯一 + 中心 sites.site_id 唯一索引兜底；离线首启无需联网即可生成。
    /// </summary>
    public static string GenerateSiteId()
    {
        Span<char> chars = stackalloc char[SiteIdRandomLength];
        Span<byte> bytes = stackalloc byte[SiteIdRandomLength];
        RandomNumberGenerator.Fill(bytes);
        for (var i = 0; i < SiteIdRandomLength; i++)
            chars[i] = SiteIdAlphabet[bytes[i] % SiteIdAlphabet.Length];
        return $"site-{new string(chars)}";
    }
}
