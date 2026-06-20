namespace NitroGateway.Infrastructure.Sqlite;

/// <summary>设备 EF 实体</summary>
public sealed class DeviceEntity
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }

    /// <summary>ProtocolIdentifier.Name</summary>
    public required string ProtocolName { get; set; }

    /// <summary>ProtocolIdentifier.Dialect</summary>
    public string? ProtocolDialect { get; set; }

    /// <summary>DeviceConnection.Endpoint</summary>
    public required string Endpoint { get; set; }

    public int ConnectTimeoutMs { get; set; } = 3000;
    public int RequestTimeoutMs { get; set; } = 5000;
    public int RetryCount { get; set; } = 3;

    /// <summary>DeviceStatus 字符串</summary>
    public required string Status { get; set; }

    /// <summary>DeviceConnection.Parameters，JSON 序列化</summary>
    public string? ConnectionParams { get; set; }

    public ICollection<PointEntity> Points { get; set; } = [];
}
