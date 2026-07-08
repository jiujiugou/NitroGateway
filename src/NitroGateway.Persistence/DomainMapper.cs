using System.Text.Json;
using NitroGateway.Domain.Devices;

namespace NitroGateway.Persistence;

/// <summary>Domain ↔ EF Entity 映射（双向）</summary>
public static class DomainMapper
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>EF 实体 → 领域模型</summary>
    public static Device ToDomain(DeviceEntity entity) => new()
    {
        Id = entity.Id,
        Name = entity.Name,
        Description = entity.Description,
        Protocol = new ProtocolIdentifier
        {
            Name = entity.ProtocolName,
            Dialect = entity.ProtocolDialect
        },
        Connection = new DeviceConnection
        {
            Endpoint = entity.Endpoint,
            ConnectTimeoutMs = entity.ConnectTimeoutMs,
            RequestTimeoutMs = entity.RequestTimeoutMs,
            RetryCount = entity.RetryCount,
            Parameters = DeserializeParams(entity.ConnectionParams)
        },
        Status = Enum.Parse<DeviceStatus>(entity.Status)
    };

    /// <summary>领域模型 → EF 实体</summary>
    public static DeviceEntity ToEntity(Device domain) => new()
    {
        Id = domain.Id,
        Name = domain.Name,
        Description = domain.Description,
        ProtocolName = domain.Protocol.Name,
        ProtocolDialect = domain.Protocol.Dialect,
        Endpoint = domain.Connection.Endpoint,
        ConnectTimeoutMs = domain.Connection.ConnectTimeoutMs,
        RequestTimeoutMs = domain.Connection.RequestTimeoutMs,
        RetryCount = domain.Connection.RetryCount,
        ConnectionParams = SerializeParams(domain.Connection.Parameters),
        Status = domain.Status.ToString()
    };

    /// <summary>EF 实体 → 领域模型</summary>
    public static DevicePoint ToDomain(PointEntity entity) => new()
    {
        Id = entity.Id,
        Name = entity.Name,
        Address = entity.Address,
        Description = entity.Description,
        DataType = Enum.Parse<DataType>(entity.DataType),
        Access = Enum.Parse<PointAccess>(entity.Access),
        Enabled = entity.Enabled,
        ScanIntervalMs = entity.ScanIntervalMs,
        Deadband = entity.Deadband,
        ScaleFactor = entity.ScaleFactor,
        ScaleOffset = entity.ScaleOffset
    };

    /// <summary>领域模型 → EF 实体</summary>
    public static PointEntity ToEntity(DevicePoint domain, Guid deviceId) => new()
    {
        Id = domain.Id,
        DeviceId = deviceId,
        Name = domain.Name,
        Address = domain.Address,
        Description = domain.Description,
        DataType = domain.DataType.ToString(),
        Access = domain.Access.ToString(),
        Enabled = domain.Enabled,
        ScanIntervalMs = domain.ScanIntervalMs,
        Deadband = domain.Deadband,
        ScaleFactor = domain.ScaleFactor,
        ScaleOffset = domain.ScaleOffset
    };

    private static Dictionary<string, object> DeserializeParams(string? json)
    {
        if (string.IsNullOrEmpty(json)) return [];
        return JsonSerializer.Deserialize<Dictionary<string, object>>(json, JsonOptions) ?? [];
    }

    private static string SerializeParams(Dictionary<string, object>? parameters)
    {
        if (parameters is null || parameters.Count == 0) return "{}";
        return JsonSerializer.Serialize(parameters, JsonOptions);
    }
}
