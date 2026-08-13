namespace NitroGateway.Storage.TimeSeries;

/// <summary>
/// 站点信息（ADR-036 中心站点管理）：唯一标识 + 可读显示名 + 来源指纹 + 冲突标记。
/// 冲突 = 同一 siteId 被不同 MQTT ClientId（机器）上报过，提示现场配置撞号。
/// </summary>
public sealed class SiteInfo
{
    /// <summary>站点唯一标识（上行 topic 第三层，不可变）</summary>
    public string SiteId { get; init; } = "";

    /// <summary>可读显示名（中心改名/绑定；空串=未命名）</summary>
    public string DisplayName { get; init; } = "";

    /// <summary>首见来源 MQTT ClientId（含机器名指纹）</summary>
    public string? SourceClientId { get; init; }

    /// <summary>最近来源 MQTT ClientId</summary>
    public string? LastSeenClientId { get; init; }

    /// <summary>首次上报时间（UTC）</summary>
    public DateTime? FirstSeenAt { get; init; }

    /// <summary>最近上报时间（UTC）</summary>
    public DateTime? LastSeenAt { get; init; }

    /// <summary>冲突标记：首见与最近来源不一致</summary>
    public bool HasConflict { get; init; }
}