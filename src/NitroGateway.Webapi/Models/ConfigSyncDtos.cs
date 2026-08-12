namespace NitroGateway.Webapi.Models;

/// <summary>配置同步导出（ADR-033 阶段 3/4）：全量设备（含 tombstone）+ 中心服务器时间</summary>
public sealed class ConfigSyncExportDto
{
    /// <summary>中心服务器时间（O 格式 UTC；现场以此校准时钟漂移判断）</summary>
    public string ServerTime { get; set; } = "";

    /// <summary>设备全量（含已删除 tombstone，IsDeleted=true）</summary>
    public List<DeviceDto> Devices { get; set; } = [];
}

/// <summary>配置同步上报请求（现场 → 中心）</summary>
public sealed class ConfigSyncPushRequest
{
    /// <summary>上报现场站点标识（当前仅记录，v1 设备表不做站点隔离）</summary>
    public string SiteId { get; set; } = "";

    /// <summary>变更列表：每台设备一条（upsert 或 tombstone）</summary>
    public List<ConfigSyncChangeDto> Changes { get; set; } = [];
}

/// <summary>单台设备的同步变更</summary>
public sealed class ConfigSyncChangeDto
{
    /// <summary>设备 upsert 负载（含点位；Deleted=true 时可为 null）</summary>
    public DeviceDto? Device { get; set; }

    /// <summary>设备 tombstone 时必填（设备 ID）</summary>
    public string? DeviceId { get; set; }

    /// <summary>现场已删除的点位 ID 列表（中心对应点位若存活则置 tombstone）</summary>
    public List<string> DeletedPointIds { get; set; } = [];

    /// <summary>true=设备删除（tombstone）；false/缺省=设备 upsert</summary>
    public bool Deleted { get; set; }
}

/// <summary>同步上报结果：逐台设备处理结论</summary>
public sealed class ConfigSyncPushResultDto
{
    public List<ConfigSyncChangeResultDto> Results { get; set; } = [];
}

/// <summary>单台设备处理结论</summary>
public sealed class ConfigSyncChangeResultDto
{
    /// <summary>设备 ID</summary>
    public string DeviceId { get; set; } = "";

    /// <summary>
    /// accepted=已应用；skipped=中心版本较新已忽略（下次下发回写现场）；
    /// rejected=中心已 tombstone 拒绝复活
    /// </summary>
    public string Action { get; set; } = "";
}
