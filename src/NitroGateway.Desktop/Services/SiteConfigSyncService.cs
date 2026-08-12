using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NitroGateway.DeviceManagement;
using NitroGateway.Domain.Devices;
using NitroGateway.Shared;

namespace NitroGateway.Desktop.Services;

/// <summary>
/// 配置自动同步服务（ADR-033 阶段 3/4，C 模型：现场临时决定权 + 中心最终裁决权）。
/// 每个周期：先拉中心快照按 UpdatedAt 双向合并下发，再清点 outbox 把现场离线改动上报中心。
/// 未配置中心地址（设置页为空）时静默跳过——仅手动导入模式；断网/鉴权失败同样跳过，
/// 不阻塞采集，下次周期补做（最终一致）。
/// </summary>
public sealed class SiteConfigSyncService : BackgroundService
{
    /// <summary>轮询间隔配置键（秒，缺省 60；下限 5 防误配刷屏）</summary>
    private const string IntervalConfigKey = "ConfigSync:PollIntervalSeconds";

    private readonly ICenterSyncSettingsStore _settingsStore;
    private readonly ICenterConfigClient _client;
    private readonly IConfigSyncOutboxStore _outbox;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly string _siteId;
    private readonly TimeSpan _interval;
    private readonly ILogger<SiteConfigSyncService> _logger;

    public SiteConfigSyncService(
        ICenterSyncSettingsStore settingsStore,
        ICenterConfigClient client,
        IConfigSyncOutboxStore outbox,
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<SiteConfigSyncService> logger)
    {
        _settingsStore = settingsStore;
        _client = client;
        _outbox = outbox;
        _scopeFactory = scopeFactory;
        _siteId = SiteOptions.Resolve(configuration["Site:Id"]);
        _interval = TimeSpan.FromSeconds(Math.Max(5, configuration.GetValue(IntervalConfigKey, 60)));
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_interval);
        while (true)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken))
                    break;
                await SyncOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // 关闭期 ODE 属正常退出，不记错误（延续 ADR-030 关闭路径干净方向）
                break;
            }
            catch (Exception ex)
            {
                // 同步失败绝不影响采集：静默跳过，下次周期补做（ADR-033 阶段 3）
                _logger.LogDebug(ex, "配置同步周期失败，下次重试");
            }
        }
    }

    /// <summary>
    /// 单轮同步：拉取中心快照 → 双向 UpdatedAt 合并 → 上报 outbox。
    /// 任一步失败即整体跳过（outbox 保留，下次重试），不抛出。
    /// </summary>
    internal async Task SyncOnceAsync(CancellationToken ct = default)
    {
        var settings = _settingsStore.Load();
        if (string.IsNullOrWhiteSpace(settings.CenterUrl))
            return; // 未配置中心：保持手动导入模式

        var centerUrl = settings.CenterUrl.Trim();
        var token = settings.CenterToken.Trim();

        var snapshotResult = await _client.FetchSyncSnapshotAsync(centerUrl, token, ct);
        if (snapshotResult.IsFailure)
        {
            _logger.LogDebug("配置同步跳过：拉取中心快照失败 {Error}", snapshotResult.Error!.Message);
            return;
        }

        await ApplySnapshotAsync(snapshotResult.Value!.Devices, ct);
        await PushPendingAsync(centerUrl, token, ct);
    }

    /// <summary>
    /// 中心快照 → 本地合并（ADR-033 阶段 3）：
    /// 中心 tombstone 删本地；中心较新覆盖本地（含点位，清 outbox）；
    /// 本地较新保留待上报；中心缺失的本地设备视为现场临时设备保留待上报。
    /// </summary>
    internal async Task ApplySnapshotAsync(IReadOnlyList<Device> centerDevices, CancellationToken ct = default)
    {
        IReadOnlyList<Device> local;
        using (var scope = _scopeFactory.CreateScope())
        {
            var manager = scope.ServiceProvider.GetRequiredService<IDeviceManager>();
            var result = await manager.GetAllAsync(ct);
            if (result.IsFailure)
            {
                _logger.LogDebug("配置同步跳过：读取本地设备失败 {Error}", result.Error!.Message);
                return;
            }
            local = result.Value!;
        }

        var localById = local.ToDictionary(d => d.Id);
        foreach (var center in centerDevices)
        {
            localById.TryGetValue(center.Id, out var localDevice);

            if (center.IsDeleted)
            {
                // 中心权威删除（tombstone）：本地硬删 + 清 outbox（不再上报已裁决的改动）
                if (localDevice is not null)
                    await DeleteLocalAsync(center.Id, ct);
                await _outbox.ClearForDeviceAsync(center.Id, ct);
                continue;
            }

            if (localDevice is null)
            {
                // 中心有、本地无：整台导入（含点位）
                await UpsertLocalAsync(center, ct);
                continue;
            }

            if (center.UpdatedAt > localDevice.UpdatedAt)
            {
                // 中心较新：以中心版本覆盖本地；本地待上报改动被裁决丢弃，清 outbox
                await UpsertLocalAsync(center, ct);
                await _outbox.ClearForDeviceAsync(center.Id, ct);
            }
            // 本地较新（现场未上报改动）：保留本地，outbox 待后续上报
        }
        // 中心快照缺失但本地存在：现场临时设备，保留待上报（新增时已写入 outbox）
    }

    /// <summary>按中心版本覆盖本地设备与点位（状态由 HealthMonitor 驱动，回退 Unknown）。</summary>
    private async Task UpsertLocalAsync(Device centerDevice, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<IDeviceManager>();
        var points = scope.ServiceProvider.GetRequiredService<IPointManager>();

        var incoming = new Device
        {
            Id = centerDevice.Id,
            Name = centerDevice.Name,
            Description = centerDevice.Description,
            Protocol = centerDevice.Protocol,
            Connection = centerDevice.Connection,
            Status = DeviceStatus.Unknown,
            UpdatedAt = centerDevice.UpdatedAt,
            IsDeleted = false
        };

        var registerResult = await manager.RegisterAsync(incoming, ct);
        if (registerResult.IsFailure)
        {
            _logger.LogDebug("配置同步：覆盖设备 {DeviceId} 失败 {Error}", centerDevice.Id, registerResult.Error!.Message);
            return;
        }

        // 点位：中心存活点位按 Id upsert；中心 tombstone 或本地多余点位本地硬删
        var livePoints = centerDevice.Points.Where(p => !p.IsDeleted).ToList();
        var liveIds = livePoints.Select(p => p.Id).ToHashSet();
        var existingResult = await points.GetByDeviceAsync(centerDevice.Id, ct);
        if (existingResult.IsSuccess)
        {
            foreach (var point in existingResult.Value!)
            {
                if (!liveIds.Contains(point.Id))
                    await points.RemoveAsync(centerDevice.Id, point.Id, ct);
            }
        }

        if (livePoints.Count > 0)
        {
            var importResult = await points.ImportAsync(centerDevice.Id, livePoints, ct);
            if (importResult.IsFailure)
                _logger.LogDebug("配置同步：导入设备 {DeviceId} 点位失败 {Error}", centerDevice.Id, importResult.Error!.Message);
        }
    }

    /// <summary>本地硬删（中心 tombstone 驱动）；UnregisterAsync 级联删点位并失效采集缓存。</summary>
    private async Task DeleteLocalAsync(Guid deviceId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<IDeviceManager>();
        var result = await manager.UnregisterAsync(deviceId, ct);
        if (result.IsFailure)
            _logger.LogDebug("配置同步：按中心 tombstone 删除设备 {DeviceId} 失败 {Error}", deviceId, result.Error!.Message);
    }

    /// <summary>
    /// outbox 上报（ADR-033 阶段 4）：按设备聚合为每台一条变更
    /// （设备删除发 tombstone；其余带设备全量状态 + 点位删除列表），
    /// 上报成功后清该设备全部 outbox 行（accepted/skipped/rejected 均不再重试，
    /// 中心裁决差异由下次下发回写本地）。
    /// </summary>
    internal async Task PushPendingAsync(string centerUrl, string token, CancellationToken ct = default)
    {
        var pendingResult = await _outbox.GetPendingAsync(ct);
        if (pendingResult.IsFailure)
        {
            _logger.LogDebug("配置同步跳过：读取 outbox 失败 {Error}", pendingResult.Error!.Message);
            return;
        }

        var changes = new List<CenterSyncChange>();
        foreach (var group in pendingResult.Value!.GroupBy(r => r.DeviceId))
        {
            var rows = group.ToList();
            if (rows.Any(r => r.Kind == ConfigSyncOutboxKind.DeviceDelete))
            {
                // 设备 tombstone 优先：点位行一并被裁决，仅上报删除
                changes.Add(new CenterSyncChange(group.Key, null, Deleted: true, []));
                continue;
            }

            // upsert 负载 = 本地设备全量状态（点位合并由中心按 UpdatedAt 裁决）
            Device? device;
            using (var scope = _scopeFactory.CreateScope())
            {
                var manager = scope.ServiceProvider.GetRequiredService<IDeviceManager>();
                var result = await manager.GetAsync(group.Key, ct);
                device = result.IsSuccess ? result.Value : null;
            }
            if (device is null)
                continue; // 设备已不存在：等下轮下发/清行处理，不构造无效负载

            var deletedPointIds = rows
                .Where(r => r.Kind == ConfigSyncOutboxKind.PointDelete && r.PointId.HasValue)
                .Select(r => r.PointId!.Value)
                .ToList();
            changes.Add(new CenterSyncChange(group.Key, device, Deleted: false, deletedPointIds));
        }

        if (changes.Count == 0)
            return;

        var pushResult = await _client.PushChangesAsync(centerUrl, token, _siteId, changes, ct);
        if (pushResult.IsFailure)
        {
            _logger.LogDebug("配置同步跳过：上报中心失败 {Error}", pushResult.Error!.Message);
            return;
        }

        foreach (var result in pushResult.Value!)
        {
            if (!Guid.TryParse(result.DeviceId, out var deviceId))
                continue;
            var clearResult = await _outbox.ClearForDeviceAsync(deviceId, ct);
            if (clearResult.IsFailure)
                _logger.LogDebug("配置同步：清除设备 {DeviceId} outbox 失败 {Error}", deviceId, clearResult.Error!.Message);
        }
        _logger.LogDebug("配置同步：上报 {Count} 台设备变更完成", changes.Count);
    }
}
