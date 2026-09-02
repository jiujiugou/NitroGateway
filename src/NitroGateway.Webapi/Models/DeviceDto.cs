namespace NitroGateway.Webapi.Models;

public sealed class DeviceDto
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Description { get; init; }
    public ProtocolDto Protocol { get; init; } = new();
    public ConnectionDto Connection { get; init; } = new();
    public string Status { get; init; } = "";
    /// <summary>设备所属站点（ADR-035 方案 A：单一归属；空串=未标注/旧数据）</summary>
    public string SiteId { get; init; } = "";
    /// <summary>配置最后修改时间（O 格式 UTC，ADR-033 阶段 3/4 同步版本依据）</summary>
    public string UpdatedAt { get; init; } = "";
    /// <summary>删除标记（tombstone，ADR-033 阶段 3/4；同步导出携带以驱动现场删除）</summary>
    public bool IsDeleted { get; init; }
    public List<PointDto> Points { get; init; } = [];
}

public sealed class ProtocolDto { public string Name { get; init; } = ""; public string? Dialect { get; init; } }

public sealed class ConnectionDto
{
    public string Endpoint { get; init; } = "";
    public int ConnectTimeoutMs { get; init; }
    public int RequestTimeoutMs { get; init; }
    public int RetryCount { get; init; }
    public int RetryIntervalMs { get; init; }
    public Dictionary<string, object> Parameters { get; init; } = [];
    /// <summary>
    /// 是否已配置连接凭据密码（仅响应回填，ADR-073 D5）。对外响应永不返回 <c>Parameters["Password"]</c>
    /// 明文，只以本标志提示前端"已设密码，留空=不改"。
    /// </summary>
    public bool HasPassword { get; init; }
}

public sealed class PointDto
{
    public string Id { get; init; } = ""; public string Name { get; init; } = ""; public string Address { get; init; } = "";
    public string? Description { get; init; } public string DataType { get; init; } = ""; public string Access { get; init; } = "";
    public bool Enabled { get; init; } public int ScanIntervalMs { get; init; }
    public double Deadband { get; init; } public double ScaleFactor { get; init; } public double ScaleOffset { get; init; }
    /// <summary>写值范围下限（null=不限，docs/14 写功能）</summary>
    public double? MinLimit { get; init; }
    /// <summary>写值范围上限（null=不限，docs/14 写功能）</summary>
    public double? MaxLimit { get; init; }
    /// <summary>配置最后修改时间（O 格式 UTC，ADR-033 阶段 3/4 同步版本依据）</summary>
    public string UpdatedAt { get; init; } = "";
    /// <summary>删除标记（tombstone，ADR-033 阶段 3/4）</summary>
    public bool IsDeleted { get; init; }
}

public sealed class DeviceStatusSummaryDto { public string DeviceId { get; init; } = ""; public string DeviceName { get; init; } = ""; public string Status { get; init; } = ""; public string? LastError { get; init; } }

