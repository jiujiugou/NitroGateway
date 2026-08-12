namespace NitroGateway.Persistence;

/// <summary>
/// 设备 EF 实体，映射 devices 表（PascalCase 列名，见 M003 迁移的历史命名说明）。
/// 与领域模型 Device 通过 <see cref=\ DomainMapper\/> 双向转换；
/// 枚举与连接参数均以字符串形式存储，避免 SQLite 对复杂类型序列化的隐式耦合。
/// </summary>
public sealed class DeviceEntity
{
    /// <summary>设备唯一标识（GUID），主键</summary>
    public Guid Id { get; set; }

    /// <summary>设备名称，必填，最长 200 字符</summary>
    public required string Name { get; set; }

    /// <summary>设备描述，可空</summary>
    public string? Description { get; set; }

    /// <summary>协议标识名，对应 ProtocolIdentifier.Name，必填，最长 100 字符</summary>
    public required string ProtocolName { get; set; }

    /// <summary>协议方言，对应 ProtocolIdentifier.Dialect（如 Modbus 的 RTU/TCP），可空</summary>
    public string? ProtocolDialect { get; set; }

    /// <summary>连接端点，对应 DeviceConnection.Endpoint（IP:端口），必填，最长 500 字符</summary>
    public required string Endpoint { get; set; }

    /// <summary>连接超时（毫秒），默认 3000，对应 DeviceConnection.ConnectTimeoutMs</summary>
    public int ConnectTimeoutMs { get; set; } = 3000;

    /// <summary>单次请求超时（毫秒），默认 5000，对应 DeviceConnection.RequestTimeoutMs</summary>
    public int RequestTimeoutMs { get; set; } = 5000;

    /// <summary>通信失败重试次数，默认 3，对应 DeviceConnection.RetryCount</summary>
    public int RetryCount { get; set; } = 3;

    /// <summary>设备通信状态，DeviceStatus 枚举字符串，必填，最长 50 字符</summary>
    public required string Status { get; set; }

    /// <summary>协议连接参数，对应 DeviceConnection.Parameters 的 JSON 序列化（CamelCase），可空</summary>
    public string? ConnectionParams { get; set; }

    /// <summary>配置最后修改时间（O 格式 UTC 字符串，ADR-033 阶段 3/4；空串=最旧）</summary>
    public string UpdatedAt { get; set; } = "";

    /// <summary>删除标记（tombstone，ADR-033 阶段 3/4；中心侧权威删除）</summary>
    public bool IsDeleted { get; set; }

    /// <summary>
    /// 设备点位导航集合。与 points 表构成一对多关系，
    /// 删除设备时级联删除其全部点位（DeleteBehavior.Cascade）。
    /// </summary>
    public ICollection<PointEntity> Points { get; set; } = [];
}
