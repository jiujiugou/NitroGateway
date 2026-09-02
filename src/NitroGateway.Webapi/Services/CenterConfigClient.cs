using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using NitroGateway.Domain.Devices;
using NitroGateway.Shared;

namespace NitroGateway.Webapi.Services;

/// <summary>中心同步快照（ADR-033 阶段 3）：全量设备（含 tombstone）+ 中心服务器时间</summary>
public sealed record CenterSyncSnapshot(IReadOnlyList<Device> Devices, DateTime ServerTime);

/// <summary>单台设备的同步变更（ADR-033 阶段 4：边缘离线改动上报；Deleted=true 时 Device 可为 null）</summary>
public sealed record CenterSyncChange(Guid DeviceId, Device? Device, bool Deleted, IReadOnlyList<Guid> DeletedPointIds);

/// <summary>单台设备的上报结论（ADR-033 阶段 4）</summary>
public sealed record CenterSyncChangeResult(string DeviceId, string Action);

/// <summary>
/// 中心配置快照/上报客户端（ADR-033 阶段 2-4）：边缘调用中心导出与上报接口。
/// 中心为独立项目（不复用 webapi），契约与桌面 CenterConfigClient 保持一致。
/// </summary>
public interface ICenterConfigClient
{
    /// <summary>
    /// 拉取中心设备/点位快照并映射为领域模型（GET /api/devices/export）。
    /// 失败（网络不可达 / 非 2xx / 响应格式异常）返回 OperationResult 失败，不抛出。
    /// </summary>
    Task<OperationResult<IReadOnlyList<Device>>> FetchSnapshotAsync(
        string centerUrl, string token, string siteId, CancellationToken ct = default);

    /// <summary>
    /// 拉取中心同步快照（GET /api/configsync/export），含中心服务器时间与全量设备（含 tombstone），
    /// 供双向 UpdatedAt 合并。失败返回 OperationResult 失败，不抛出。
    /// </summary>
    Task<OperationResult<CenterSyncSnapshot>> FetchSyncSnapshotAsync(
        string centerUrl, string token, string siteId, CancellationToken ct = default);

    /// <summary>
    /// 上报边缘离线改动（POST /api/configsync/push），返回逐台设备结论（accepted/skipped/rejected）；
    /// 网络或业务失败返回 OperationResult 失败，不抛出。
    /// </summary>
    Task<OperationResult<IReadOnlyList<CenterSyncChangeResult>>> PushChangesAsync(
        string centerUrl, string token, string siteId, IReadOnlyList<CenterSyncChange> changes,
        CancellationToken ct = default);
}

/// <summary>
/// 中心导出接口的 HTTP 客户端实现。
/// 地址与 Token 为运行时配置（ConfigSync:CenterUrl / ConfigSync:CenterToken），
/// 独立使用 HttpClient 而非 Transport.IHttpClient（后者为 Forwarder 固定 HttpConnectionOptions 解耦）。
/// </summary>
public sealed class CenterConfigClient : ICenterConfigClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;

    /// <param name="http">HttpClient；DI 注入的实例已配置超时，测试可用自定义 handler 构造</param>
    public CenterConfigClient(HttpClient http) => _http = http;

    /// <summary>测试专用：用自定义 handler 构造（拦截请求，不真正出网）。</summary>
    internal CenterConfigClient(HttpMessageHandler handler)
        : this(new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) })
    {
    }

    public async Task<OperationResult<IReadOnlyList<Device>>> FetchSnapshotAsync(
        string centerUrl, string token, string siteId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(centerUrl))
            return OperationalError.Validation("中心地址不能为空");

        var baseUrl = centerUrl.Trim().TrimEnd('/');
        // ADR-035 方案 A：按站点过滤导出（边缘只导入本站点设备）
        var url = string.IsNullOrWhiteSpace(siteId) ? $"{baseUrl}/api/devices/export" : $"{baseUrl}/api/devices/export?siteId=" + Uri.EscapeDataString(siteId);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());

        try
        {
            using var response = await _http.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                return response.StatusCode == HttpStatusCode.Unauthorized
                    ? OperationalError.Validation("鉴权失败（401）：Token 无效或已过期")
                    : OperationalError.General($"中心返回 {(int)response.StatusCode}：{Truncate(body)}");

            var parsed = JsonSerializer.Deserialize<CenterSnapshotResponse>(body, JsonOptions);
            if (parsed is null || !parsed.Success || parsed.Data is null)
                return OperationalError.General($"中心响应格式不正确：{Truncate(body)}");

            return parsed.Data.Select(ToDomain).ToList();
        }
        catch (HttpRequestException ex)
        {
            return OperationalError.General($"无法连接中心：{ex.Message}");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return OperationalError.General("连接中心超时");
        }
        catch (JsonException ex)
        {
            return OperationalError.General($"中心响应解析失败：{ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<OperationResult<CenterSyncSnapshot>> FetchSyncSnapshotAsync(
        string centerUrl, string token, string siteId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(centerUrl))
            return OperationalError.Validation("中心地址不能为空");

        var baseUrl = centerUrl.Trim().TrimEnd('/');
        // ADR-035 方案 A：按站点过滤下发（边缘只同步本站点设备）
        var url = string.IsNullOrWhiteSpace(siteId) ? $"{baseUrl}/api/configsync/export" : $"{baseUrl}/api/configsync/export?siteId=" + Uri.EscapeDataString(siteId);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());

        try
        {
            using var response = await _http.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                return response.StatusCode == HttpStatusCode.Unauthorized
                    ? OperationalError.Validation("鉴权失败（401）：Token 无效或已过期")
                    : OperationalError.General($"中心返回 {(int)response.StatusCode}：{Truncate(body)}");

            var parsed = JsonSerializer.Deserialize<CenterSyncExportResponse>(body, JsonOptions);
            if (parsed is null || !parsed.Success || parsed.Data is null || parsed.Data.Devices is null)
                return OperationalError.General($"中心响应格式不正确：{Truncate(body)}");

            var serverTime = ParseTime(parsed.Data.ServerTime);
            return new CenterSyncSnapshot(parsed.Data.Devices.Select(ToSyncDomain).ToList(), serverTime);
        }
        catch (HttpRequestException ex)
        {
            return OperationalError.General($"无法连接中心：{ex.Message}");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return OperationalError.General("连接中心超时");
        }
        catch (JsonException ex)
        {
            return OperationalError.General($"中心响应解析失败：{ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<OperationResult<IReadOnlyList<CenterSyncChangeResult>>> PushChangesAsync(
        string centerUrl, string token, string siteId, IReadOnlyList<CenterSyncChange> changes,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(centerUrl))
            return OperationalError.Validation("中心地址不能为空");
        if (changes.Count == 0)
            return OperationResult<IReadOnlyList<CenterSyncChangeResult>>.Success([]);

        var baseUrl = centerUrl.Trim().TrimEnd('/');
        var payload = new CenterSyncPushPayload
        {
            SiteId = siteId,
            Changes = changes.Select(c => new CenterSyncChangePayload
            {
                Deleted = c.Deleted,
                DeviceId = c.Deleted ? c.DeviceId.ToString() : null,
                Device = c.Deleted ? null : ToCenterDeviceDto(c.Device!),
                DeletedPointIds = c.DeletedPointIds.Select(id => id.ToString()).ToList()
            }).ToList()
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/configsync/push")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, JsonOptions),
                System.Text.Encoding.UTF8, "application/json")
        };
        if (!string.IsNullOrWhiteSpace(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());

        try
        {
            using var response = await _http.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                return response.StatusCode == HttpStatusCode.Unauthorized
                    ? OperationalError.Validation("鉴权失败（401）：Token 无效或已过期")
                    : OperationalError.General($"中心返回 {(int)response.StatusCode}：{Truncate(body)}");

            var parsed = JsonSerializer.Deserialize<CenterSyncPushResponse>(body, JsonOptions);
            if (parsed is null || !parsed.Success || parsed.Data is null)
                return OperationalError.General($"中心响应格式不正确：{Truncate(body)}");

            return parsed.Data.Results?
                .Select(r => new CenterSyncChangeResult(r.DeviceId ?? "", r.Action ?? ""))
                .ToList() ?? [];
        }
        catch (HttpRequestException ex)
        {
            return OperationalError.General($"无法连接中心：{ex.Message}");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return OperationalError.General("连接中心超时");
        }
        catch (JsonException ex)
        {
            return OperationalError.General($"中心响应解析失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 快照 DTO → 领域模型。状态强制回退 Unknown（ADR-029：设备状态由 HealthMonitor 驱动，
    /// 导入不伪造 Online/Offline）；枚举字符串容错回退默认值，与 DomainMapper 语义一致。
    /// </summary>
    private static Device ToDomain(CenterDeviceDto d)
    {
        var device = new Device
        {
            Id = Guid.TryParse(d.Id, out var id) ? id : Guid.Empty,
            Name = d.Name ?? "",
            Description = d.Description,
            Protocol = new ProtocolIdentifier { Name = d.Protocol?.Name ?? "", Dialect = d.Protocol?.Dialect },
            Connection = new DeviceConnection
            {
                Endpoint = d.Connection?.Endpoint ?? "",
                ConnectTimeoutMs = d.Connection?.ConnectTimeoutMs ?? 3000,
                RequestTimeoutMs = d.Connection?.RequestTimeoutMs ?? 5000,
                RetryCount = d.Connection?.RetryCount ?? 3,
                RetryIntervalMs = d.Connection?.RetryIntervalMs ?? 1000,
                Parameters = d.Connection?.Parameters ?? []
            },
            Status = DeviceStatus.Unknown,
            // ADR-035 方案 A：导入保留中心站点归属（本地设备行带 site，上报往返一致）
            SiteId = d.SiteId ?? "",
            // ADR-033 阶段 3/4：手动导入保留中心时间戳，导入后本地与中心版本对齐（避免下次同步误判本地较旧）
            UpdatedAt = ParseTime(d.UpdatedAt),
            IsDeleted = d.IsDeleted
        };
        // 手动导入=以中心为准重置本地：中心 tombstone 的点位不落本地（本地不保留删除标记）
        foreach (var point in (d.Points ?? []).Where(p => !p.IsDeleted))
            device.AddPoint(ToPoint(point));
        return device;
    }

    /// <summary>
    /// 同步快照 DTO → 领域模型（ADR-033 阶段 3）：保留 tombstone 与时间戳，
    /// 供下发双向 UpdatedAt 合并判断。设备状态仍回退 Unknown（状态由本地 HealthMonitor 驱动）。
    /// </summary>
    private static Device ToSyncDomain(CenterDeviceDto d)
    {
        var device = new Device
        {
            Id = Guid.TryParse(d.Id, out var id) ? id : Guid.Empty,
            Name = d.Name ?? "",
            Description = d.Description,
            Protocol = new ProtocolIdentifier { Name = d.Protocol?.Name ?? "", Dialect = d.Protocol?.Dialect },
            Connection = new DeviceConnection
            {
                Endpoint = d.Connection?.Endpoint ?? "",
                ConnectTimeoutMs = d.Connection?.ConnectTimeoutMs ?? 3000,
                RequestTimeoutMs = d.Connection?.RequestTimeoutMs ?? 5000,
                RetryCount = d.Connection?.RetryCount ?? 3,
                RetryIntervalMs = d.Connection?.RetryIntervalMs ?? 1000,
                Parameters = d.Connection?.Parameters ?? []
            },
            Status = DeviceStatus.Unknown,
            SiteId = d.SiteId ?? "",
            UpdatedAt = ParseTime(d.UpdatedAt),
            IsDeleted = d.IsDeleted
        };
        foreach (var point in d.Points ?? [])
            device.AddPoint(ToSyncPoint(point));
        return device;
    }

    private static DevicePoint ToPoint(CenterPointDto p) => new()
    {
        Id = Guid.TryParse(p.Id, out var id) ? id : Guid.Empty,
        Name = p.Name ?? "",
        Address = p.Address ?? "",
        Description = p.Description,
        DataType = Enum.TryParse<DataType>(p.DataType, ignoreCase: true, out var dataType) ? dataType : default,
        Access = Enum.TryParse<PointAccess>(p.Access, ignoreCase: true, out var access) ? access : default,
        Enabled = p.Enabled,
        ScanIntervalMs = p.ScanIntervalMs,
        Deadband = p.Deadband,
        ScaleFactor = p.ScaleFactor,
        ScaleOffset = p.ScaleOffset,
        UpdatedAt = ParseTime(p.UpdatedAt),
        IsDeleted = p.IsDeleted
    };

    /// <summary>同步快照点位映射：保留 tombstone 与时间戳（ADR-033 阶段 3）</summary>
    private static DevicePoint ToSyncPoint(CenterPointDto p) => new()
    {
        Id = Guid.TryParse(p.Id, out var id) ? id : Guid.Empty,
        Name = p.Name ?? "",
        Address = p.Address ?? "",
        Description = p.Description,
        DataType = Enum.TryParse<DataType>(p.DataType, ignoreCase: true, out var dataType) ? dataType : default,
        Access = Enum.TryParse<PointAccess>(p.Access, ignoreCase: true, out var access) ? access : default,
        Enabled = p.Enabled,
        ScanIntervalMs = p.ScanIntervalMs,
        Deadband = p.Deadband,
        ScaleFactor = p.ScaleFactor,
        ScaleOffset = p.ScaleOffset,
        UpdatedAt = ParseTime(p.UpdatedAt),
        IsDeleted = p.IsDeleted
    };

    /// <summary>本地设备 → 中心上报 DTO（ADR-033 阶段 4：保留 UpdatedAt，状态随当前值）</summary>
    private static CenterDeviceDto ToCenterDeviceDto(Device device) => new()
    {
        Id = device.Id.ToString(),
        Name = device.Name,
        Description = device.Description,
        Protocol = new CenterProtocolDto { Name = device.Protocol.Name, Dialect = device.Protocol.Dialect },
        Connection = new CenterConnectionDto
        {
            Endpoint = device.Connection.Endpoint,
            ConnectTimeoutMs = device.Connection.ConnectTimeoutMs,
            RequestTimeoutMs = device.Connection.RequestTimeoutMs,
            RetryCount = device.Connection.RetryCount,
            RetryIntervalMs = device.Connection.RetryIntervalMs,
            // ADR-073 D5：上报中心绝不携带明文密码（中心侧无解密密钥），只保留其余参数
            Parameters = DeviceParamRedaction.WithoutPassword(device.Connection.Parameters)
        },
        Status = device.Status.ToString(),
        SiteId = device.SiteId ?? "",
        UpdatedAt = FormatTime(device.UpdatedAt),
        IsDeleted = device.IsDeleted,
        Points = device.Points.Select(p => new CenterPointDto
        {
            Id = p.Id.ToString(),
            Name = p.Name,
            Address = p.Address,
            Description = p.Description,
            DataType = p.DataType.ToString(),
            Access = p.Access.ToString(),
            Enabled = p.Enabled,
            ScanIntervalMs = p.ScanIntervalMs,
            Deadband = p.Deadband,
            ScaleFactor = p.ScaleFactor,
            ScaleOffset = p.ScaleOffset,
            UpdatedAt = FormatTime(p.UpdatedAt),
            IsDeleted = p.IsDeleted
        }).ToList()
    };

    /// <summary>解析 O 格式 UTC 时间；空串/非法 → MinValue（最旧）</summary>
    private static DateTime ParseTime(string? value)
        => string.IsNullOrEmpty(value)
            ? DateTime.MinValue
            : DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed.ToUniversalTime()
                : DateTime.MinValue;

    /// <summary>序列化 O 格式 UTC 时间；MinValue 存空串（最旧，与中心语义一致）</summary>
    private static string FormatTime(DateTime value)
        => value == DateTime.MinValue ? "" : value.ToUniversalTime().ToString("O");

    private static string Truncate(string text)
        => text.Length <= 200 ? text : text[..200] + "…";

    // ── 中心导出快照 JSON 的解析模型（与 Webapi DeviceDto/PointDto 序列化形态对应）──
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
        /// <summary>删除标记（tombstone，ADR-033 阶段 3/4）</summary>
        public bool IsDeleted { get; set; }
    }

    // ── 配置同步导出/上报的 JSON 解析模型（与中心 ConfigSyncDtos 序列化形态对应）──
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

    /// <summary>同步上报请求（POST /api/configsync/push，边缘离线改动）</summary>
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
}
