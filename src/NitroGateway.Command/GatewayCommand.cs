namespace NitroGateway.Command;

/// <summary>
/// 已解析的下行写值命令（来自 <c>nitrogateway/{siteId}/{deviceId}/commands</c> topic）。
/// 载荷不含 deviceId——deviceId 从 topic 第 3 段解析；siteId 从 topic 第 2 段解析（供回执 topic 复用）。
/// </summary>
public sealed record GatewayCommand
{
    /// <summary>命令 ID（云侧幂等键；重试不换）</summary>
    public required Guid CommandId { get; init; }

    /// <summary>命令类型（当前仅支持 WritePoint）</summary>
    public required string Type { get; init; }

    /// <summary>站点标识（topic 第 2 段，须与本地 Site:Id 一致）</summary>
    public required string SiteId { get; init; }

    /// <summary>目标设备 ID（topic 第 3 段）</summary>
    public required Guid DeviceId { get; init; }

    /// <summary>目标点位 ID（载荷 pointId）</summary>
    public required Guid PointId { get; init; }

    /// <summary>写入值（载荷 value，已解包为 CLR 原始类型 long/double/string/bool）</summary>
    public required object Value { get; init; }

    /// <summary>云侧发起时间（ISO 8601 带偏移；缺失时回退当前 UTC）</summary>
    public required DateTimeOffset RequestedAt { get; init; }
}
