using System.Text.Json;
using NitroGateway.Domain.Devices;
using System.Globalization;

namespace NitroGateway.Persistence;

public static class DomainMapper
{
    /// <summary>连接参数 JSON 序列化选项：CamelCase 属性命名，与前端 DTO 约定一致</summary>
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
        Status = ParseEnum<DeviceStatus>(entity.Status),
        // ADR-033 阶段 3/4：同步版本字段；空串（旧数据）等价 DateTime.MinValue（最旧）
        UpdatedAt = ParseUpdatedAt(entity.UpdatedAt),
        IsDeleted = entity.IsDeleted,
        // ADR-035 方案 A：设备站点归属（空串=未标注）
        SiteId = entity.SiteId ?? ""
    };

    /// <summary>
    /// 领域模型 → EF 实体。
    /// <paramref name="parameterTransform"/> 为可选连接参数变换（ADR-073 D5：仓储在写库前用它加密
    /// OPC UA Password，实现"落库密文、域内明文"边界）；缺省不传保持原行为，既有直接调用方不受影响。
    /// 变换仅作用于序列化前的参数副本，不修改入参 <paramref name="domain"/>。
    /// </summary>
    public static DeviceEntity ToEntity(
        Device domain,
        Func<Dictionary<string, object>, Dictionary<string, object>>? parameterTransform = null)
    {
        var parameters = parameterTransform is null
            ? domain.Connection.Parameters
            : parameterTransform(domain.Connection.Parameters);
        return new DeviceEntity
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
            ConnectionParams = SerializeParams(parameters),
            Status = domain.Status.ToString(),
            UpdatedAt = FormatUpdatedAt(domain.UpdatedAt),
            IsDeleted = domain.IsDeleted,
            // ADR-035 方案 A：设备站点归属
            SiteId = domain.SiteId ?? ""
        };
    }

    /// <summary>EF 实体 → 领域模型</summary>
    public static DevicePoint ToDomain(PointEntity entity) => new()
    {
        Id = entity.Id,
        Name = entity.Name,
        Address = entity.Address,
        Description = entity.Description,
        DataType = ParseEnum<DataType>(entity.DataType),
        Access = ParseEnum<PointAccess>(entity.Access),
        Enabled = entity.Enabled,
        ScanIntervalMs = entity.ScanIntervalMs,
        Deadband = entity.Deadband,
        ScaleFactor = entity.ScaleFactor,
        ScaleOffset = entity.ScaleOffset,
        MinLimit = entity.MinLimit,
        MaxLimit = entity.MaxLimit,
        UpdatedAt = ParseUpdatedAt(entity.UpdatedAt),
        IsDeleted = entity.IsDeleted
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
        ScaleOffset = domain.ScaleOffset,
        MinLimit = domain.MinLimit,
        MaxLimit = domain.MaxLimit,
        UpdatedAt = FormatUpdatedAt(domain.UpdatedAt),
        IsDeleted = domain.IsDeleted
    };

    /// <summary>
    /// 解析存储的 UpdatedAt 字符串（O 格式 UTC）：空串/非法回退 <see cref="DateTime.MinValue"/>（最旧），
    /// 保证旧数据首次同步时会被任意新版本覆盖。
    /// </summary>
    internal static DateTime ParseUpdatedAt(string? value)
        => string.IsNullOrEmpty(value)
            ? DateTime.MinValue
            : DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed.ToUniversalTime()
                : DateTime.MinValue;

    /// <summary>
    /// 序列化 UpdatedAt 为 O 格式 UTC 字符串；<see cref="DateTime.MinValue"/> 存空串（最旧，兼容旧数据语义）。
    /// </summary>
    internal static string FormatUpdatedAt(DateTime value)
        => value == DateTime.MinValue ? "" : value.ToUniversalTime().ToString("O");

    internal static T ParseEnum<T>(string? value) where T : struct, Enum
        => Enum.TryParse<T>(value, ignoreCase: true, out var result) ? result : default;

    /// <summary>
    /// 解析连接参数 JSON；null/空串返回空字典，避免调用方判空。
    /// </summary>
    private static Dictionary<string, object> DeserializeParams(string? json)
    {
        if (string.IsNullOrEmpty(json)) return [];
        return JsonSerializer.Deserialize<Dictionary<string, object>>(json, JsonOptions) ?? [];
    }

    /// <summary>
    /// 序列化连接参数为 CamelCase JSON；null/空字典返回 "{}" 以保持列非空语义。
    /// </summary>
    private static string SerializeParams(Dictionary<string, object>? parameters)
    {
        if (parameters is null || parameters.Count == 0) return "{}";
        return JsonSerializer.Serialize(parameters, JsonOptions);
    }
}



