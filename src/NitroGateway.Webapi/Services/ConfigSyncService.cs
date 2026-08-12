using System.Globalization;
using NitroGateway.DeviceManagement;
using NitroGateway.Domain.Devices;
using NitroGateway.Protocols;
using NitroGateway.Webapi.Models;

namespace NitroGateway.Webapi.Services;

/// <summary>
/// 中心侧配置同步接收（ADR-033 阶段 3/4，C 模型：现场临时决定权 + 中心最终裁决权）。
/// 合并规则：
/// <list type="bullet">
/// <item>中心已 tombstone 的设备：拒绝现场复活（权威删除不可逆，现场应改用新 Id）；</item>
/// <item>中心版本较新（UpdatedAt 更大）：整台跳过，下次下发以中心版本回写现场并清 dirty；</item>
/// <item>点位级同样按 UpdatedAt 双向合并；中心 tombstone 的点位拒绝复活；</item>
/// <item>现场上报的 deletedPointIds 中心若存活则置 tombstone（时间取 max 防时钟回拨）。</item>
/// </list>
/// </summary>
public sealed class ConfigSyncService
{
    private readonly IDeviceManager _devices;
    private readonly IPointManager _points;

    public ConfigSyncService(IDeviceManager devices, IPointManager points)
    {
        _devices = devices;
        _points = points;
    }

    /// <summary>逐条应用现场上报的变更，返回每台设备的处理结论。</summary>
    public async Task<ConfigSyncPushResultDto> ApplyAsync(
        ConfigSyncPushRequest request, CancellationToken ct = default)
    {
        var results = new List<ConfigSyncChangeResultDto>();
        foreach (var change in request.Changes)
        {
            results.Add(change.Deleted || change.Device is null
                ? await ApplyDeviceTombstoneAsync(change, ct)
                : await ApplyDeviceUpsertAsync(change, request.SiteId, ct));
        }
        return new ConfigSyncPushResultDto { Results = results };
    }

    /// <summary>设备 tombstone：中心存活则软删；已删则幂等成功（accepted）。</summary>
    private async Task<ConfigSyncChangeResultDto> ApplyDeviceTombstoneAsync(
        ConfigSyncChangeDto change, CancellationToken ct)
    {
        var deviceIdText = change.DeviceId ?? change.Device?.Id ?? "";
        if (!Guid.TryParse(deviceIdText, out var deviceId))
            return Result(deviceIdText, "rejected");

        var existing = await _devices.GetIncludingDeletedAsync(deviceId, ct);
        if (existing.IsSuccess && existing.Value!.IsDeleted)
            return Result(deviceIdText, "accepted");

        var r = await _devices.SoftDeleteAsync(deviceId, ct);
        return r.IsSuccess ? Result(deviceIdText, "accepted") : Result(deviceIdText, "rejected");
    }

    /// <summary>设备 upsert：tombstone 拒绝 → 中心较新跳过 → 否则按点位级 UpdatedAt 合并落库。</summary>
    private async Task<ConfigSyncChangeResultDto> ApplyDeviceUpsertAsync(
        ConfigSyncChangeDto change, string siteId, CancellationToken ct)
    {
        var dto = change.Device!;
        if (!Guid.TryParse(dto.Id, out var deviceId))
            return Result(dto.Id, "rejected");

        var incomingAt = ParseTime(dto.UpdatedAt);
        var existing = await _devices.GetIncludingDeletedAsync(deviceId, ct);

        // 1) 中心已删 → 拒绝复活
        if (existing.IsSuccess && existing.Value!.IsDeleted)
            return Result(dto.Id, "rejected");

        // 2) 中心较新 → 整台跳过（点位删除一并跳过，下次下发以中心版本回写现场）
        if (existing.IsSuccess && existing.Value!.UpdatedAt > incomingAt)
            return Result(dto.Id, "skipped");

        // 3) 点位级合并
        var currentPoints = existing.IsSuccess ? existing.Value!.Points : [];
        var tombstonedIds = currentPoints.Where(p => p.IsDeleted).Select(p => p.Id).ToHashSet();
        var currentLive = currentPoints.Where(p => !p.IsDeleted).ToDictionary(p => p.Id);
        var incomingIds = dto.Points.Select(p => Guid.TryParse(p.Id, out var id) ? id : Guid.Empty).ToHashSet();
        var deletedIds = change.DeletedPointIds
            .Select(s => Guid.TryParse(s, out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty)
            .ToHashSet();

        var merged = new List<DevicePoint>();

        // 3a) 现场删除的点位 → 中心 tombstone（时间取 max，防现场时钟回拨覆盖更新的中心删除）
        foreach (var id in deletedIds)
        {
            if (!currentLive.TryGetValue(id, out var point) || point.IsDeleted)
                continue;
            point.IsDeleted = true;
            if (point.UpdatedAt <= incomingAt)
                point.UpdatedAt = incomingAt;
            merged.Add(point);
        }

        // 3b) 未上报也未删除的中心存活点位 → 保留（下次下发送回现场，避免现场删除中心未删的点）
        foreach (var kv in currentLive)
        {
            if (!incomingIds.Contains(kv.Key) && !deletedIds.Contains(kv.Key))
                merged.Add(kv.Value);
        }

        // 3c) 上报点位：中心 tombstone 的拒绝复活；中心较新的保留中心；否则采用现场版本
        foreach (var pointDto in dto.Points)
        {
            if (!Guid.TryParse(pointDto.Id, out var pointId))
                continue;
            if (tombstonedIds.Contains(pointId))
                continue;
            if (currentLive.TryGetValue(pointId, out var current)
                && current.UpdatedAt > ParseTime(pointDto.UpdatedAt))
            {
                merged.Add(current);
                continue;
            }
            merged.Add(ToPoint(pointDto));
        }

        // 4) 落库：设备行保留现场时间戳（≥ 中心），点位批量 upsert
        var device = ToDevice(dto, siteId);
        var register = await _devices.RegisterAsync(device, ct);
        if (register.IsFailure)
            return Result(dto.Id, "rejected");

        var import = await _points.ImportAsync(deviceId, merged, ct);
        return import.IsFailure ? Result(dto.Id, "rejected") : Result(dto.Id, "accepted");
    }

    /// <summary>设备 DTO → 领域模型（同步路径：保留现场 UpdatedAt，不重新盖章）</summary>
    private static Device ToDevice(DeviceDto d, string? siteId)
    {
        var device = new Device
        {
            Id = Guid.TryParse(d.Id, out var id) ? id : Guid.Empty,
            Name = d.Name ?? "",
            Description = d.Description,
            Protocol = new ProtocolIdentifier { Name = d.Protocol?.Name ?? "", Dialect = d.Protocol?.Dialect },
            Connection = BuildConnection(d.Connection),
            Status = Enum.TryParse<DeviceStatus>(d.Status, out var status) ? status : DeviceStatus.Unknown,
            UpdatedAt = ParseTime(d.UpdatedAt),
            IsDeleted = d.IsDeleted,
            // ADR-035 方案 A：设备归属 = 上报方站点（现场即归属，中心 Web 修改站点由管理员负责）
            SiteId = siteId ?? d.SiteId ?? ""
        };
        return device;
    }

    /// <summary>点位 DTO → 领域模型（同步路径：保留现场 UpdatedAt）</summary>
    private static DevicePoint ToPoint(PointDto p) => new()
    {
        Id = Guid.TryParse(p.Id, out var id) ? id : Guid.Empty,
        Name = p.Name ?? "",
        Address = p.Address ?? "",
        Description = p.Description,
        DataType = Enum.TryParse<DataType>(p.DataType, out var dataType) ? dataType : default,
        Access = Enum.TryParse<PointAccess>(p.Access, out var access) ? access : PointAccess.ReadOnly,
        Enabled = p.Enabled,
        ScanIntervalMs = p.ScanIntervalMs,
        Deadband = p.Deadband,
        ScaleFactor = p.ScaleFactor,
        ScaleOffset = p.ScaleOffset,
        UpdatedAt = ParseTime(p.UpdatedAt),
        IsDeleted = p.IsDeleted
    };

    private static DeviceConnection BuildConnection(ConnectionDto? c) => c is null
        ? new DeviceConnection { Endpoint = "" }
        : new DeviceConnection
        {
            Endpoint = c.Endpoint ?? "",
            ConnectTimeoutMs = c.ConnectTimeoutMs,
            RequestTimeoutMs = c.RequestTimeoutMs,
            RetryCount = c.RetryCount,
            RetryIntervalMs = c.RetryIntervalMs,
            Parameters = c.Parameters
        };

    /// <summary>解析 O 格式时间；空串/非法 → MinValue（最旧，等价"任意新版本覆盖"）</summary>
    internal static DateTime ParseTime(string? value)
        => string.IsNullOrEmpty(value)
            ? DateTime.MinValue
            : DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed.ToUniversalTime()
                : DateTime.MinValue;

    private static ConfigSyncChangeResultDto Result(string deviceId, string action)
        => new() { DeviceId = deviceId, Action = action };
}



