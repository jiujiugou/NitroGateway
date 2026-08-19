namespace NitroGateway.Desktop.Services.Sync;

// ADR-033 阶段 2：中心导出快照 JSON 的解析模型（与 Webapi DeviceDto/PointDto 序列化形状对应）。
// 桌面不引用 Webapi 项目，此处定义最小可解析子集；枚举以字符串传输，映射时容错回退默认值。

internal sealed class CenterSnapshotResponse
{
    public bool Success { get; set; }
    public List<CenterDeviceDto>? Data { get; set; }
}

internal sealed class CenterDeviceDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public CenterProtocolDto? Protocol { get; set; }
    public CenterConnectionDto? Connection { get; set; }
    public string Status { get; set; } = "";
    /// <summary>设备所属站点（ADR-035 方案 A：单一归属；空串=未标注）</summary>
    public string SiteId { get; set; } = "";
    public List<CenterPointDto>? Points { get; set; }

    /// <summary>同步版本时间戳（O 格式 UTC；空串=最旧，ADR-033 阶段 3/4）</summary>
    public string UpdatedAt { get; set; } = "";

    /// <summary>删除标记（tombstone，中心权威删除；ADR-033 阶段 3/4）</summary>
    public bool IsDeleted { get; set; }
}

internal sealed class CenterProtocolDto
{
    public string Name { get; set; } = "";
    public string? Dialect { get; set; }
}

internal sealed class CenterConnectionDto
{
    public string Endpoint { get; set; } = "";
    public int ConnectTimeoutMs { get; set; }
    public int RequestTimeoutMs { get; set; }
    public int RetryCount { get; set; }
    public int RetryIntervalMs { get; set; }
    public Dictionary<string, object> Parameters { get; set; } = [];
}

internal sealed class CenterPointDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Address { get; set; } = "";
    public string? Description { get; set; }
    public string DataType { get; set; } = "";
    public string Access { get; set; } = "";
    public bool Enabled { get; set; }
    public int ScanIntervalMs { get; set; }
    public double Deadband { get; set; }
    public double ScaleFactor { get; set; }
    public double ScaleOffset { get; set; }

    /// <summary>同步版本时间戳（O 格式 UTC；空串=最旧，ADR-033 阶段 3/4）</summary>
    public string UpdatedAt { get; set; } = "";

    /// <summary>删除标记（tombstone，中心权威删除；ADR-033 阶段 3/4）</summary>
    public bool IsDeleted { get; set; }
}

// ADR-033 阶段 3/4：配置同步导出/上报的 JSON 解析模型（与 Webapi ConfigSyncDtos 序列化形状对应）。

/// <summary>同步导出响应（GET /api/configsync/export，含中心服务器时间）</summary>
internal sealed class CenterSyncExportResponse
{
    public bool Success { get; set; }
    public CenterSyncExportData? Data { get; set; }
}

internal sealed class CenterSyncExportData
{
    public string ServerTime { get; set; } = "";
    public List<CenterDeviceDto>? Devices { get; set; }
}

/// <summary>同步上报请求（POST /api/configsync/push，现场离线改动）</summary>
internal sealed class CenterSyncPushPayload
{
    public string SiteId { get; set; } = "";
    public List<CenterSyncChangePayload> Changes { get; set; } = [];
}

internal sealed class CenterSyncChangePayload
{
    public CenterDeviceDto? Device { get; set; }
    public string? DeviceId { get; set; }
    public List<string> DeletedPointIds { get; set; } = [];
    public bool Deleted { get; set; }
}

/// <summary>同步上报响应：逐台设备处理结论</summary>
internal sealed class CenterSyncPushResponse
{
    public bool Success { get; set; }
    public CenterSyncPushResultData? Data { get; set; }
}

internal sealed class CenterSyncPushResultData
{
    public List<CenterSyncChangeResultData>? Results { get; set; }
}

internal sealed class CenterSyncChangeResultData
{
    public string DeviceId { get; set; } = "";
    public string Action { get; set; } = "";
}

