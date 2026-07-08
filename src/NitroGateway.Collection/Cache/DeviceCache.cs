using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NitroGateway.DeviceManagement;
using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Events;
using NitroGateway.Shared;

namespace NitroGateway.Collection.Cache;

/// <summary>
/// 设备运行时缓存。采集线程始终从此读取，不查数据库。
/// 配置变更通过 <see cref="IDeviceChangeSink"/> 增量更新，无需全量重载。
/// </summary>
public sealed class DeviceCache : IDeviceChangeSink
{
    private readonly ConcurrentDictionary<Guid, Device> _devices = new();
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DeviceCache> _logger;
    private volatile int _version;
    private bool _loaded;

    /// <summary>当前缓存版本号。采集线程可比较版本来决定是否切换到新配置</summary>
    public int Version => _version;

    /// <summary>缓存中的设备总数</summary>
    public int Count => _devices.Count;

    public DeviceCache(IServiceScopeFactory scopeFactory, ILogger<DeviceCache> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    // ════════════════════════════════════════════
    //  初始化
    // ════════════════════════════════════════════

    /// <summary>启动时调用一次，从数据库全量加载</summary>
    public async Task LoadAsync(IDeviceManager deviceManager, CancellationToken ct = default)
    {
        if (_loaded) return;

        var result = await deviceManager.GetAllAsync(ct);
        if (result.IsFailure)
        {
            _logger.LogError("DeviceCache 初始化加载失败: {Error}", result.Error!.Message);
            return;
        }

        foreach (var device in result.Value!)
            _devices[device.Id] = device;

        _version = 1;
        _loaded = true;
        _logger.LogInformation("DeviceCache 初始化完成: {Count} 台设备", _devices.Count);
    }

    // ════════════════════════════════════════════
    //  查询 — 采集线程入口
    // ════════════════════════════════════════════

    /// <summary>获取所有 Online 设备的快照</summary>
    public IReadOnlyList<Device> GetOnlineDevices()
    {
        return _devices.Values
            .Where(d => d.Status == DeviceStatus.Online)
            .ToList();
    }

    /// <summary>按 ID 获取单台设备</summary>
    public Device? Get(Guid deviceId)
    {
        _devices.TryGetValue(deviceId, out var d);
        return d;
    }

    // ════════════════════════════════════════════
    //  IDeviceChangeSink — 配置变更增量更新
    // ════════════════════════════════════════════

    /// <inheritdoc />
    public void OnDeviceChanged(DeviceChangeEvent e)
    {
        switch (e.Type)
        {
            case DeviceChangeType.Added:
            case DeviceChangeType.Updated:
            case DeviceChangeType.PointsChanged:
                // 需要重新加载该设备（点位移除/更新涉及设备完整状态）
                _ = ReloadDeviceAsync(e.DeviceId);
                break;

            case DeviceChangeType.Removed:
                _devices.TryRemove(e.DeviceId, out _);
                _version++;
                _logger.LogInformation("DeviceCache 移除: {DeviceId} (v{Version})", e.DeviceId, _version);
                break;
        }
    }

    // ════════════════════════════════════════════
    //  ReloadDevice 通过 IServiceScopeFactory
    //  因为 DeviceCache 是 Singleton，而 IDeviceManager 是 Scoped
    // ════════════════════════════════════════════

    private async Task ReloadDeviceAsync(Guid deviceId)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var manager = scope.ServiceProvider.GetRequiredService<IDeviceManager>();
            var result = await manager.GetAsync(deviceId);

            if (result.IsSuccess && result.Value is not null)
            {
                _devices[result.Value.Id] = result.Value;
                _version++;
                _logger.LogInformation("DeviceCache 更新: {DeviceId} (v{Version})", deviceId, _version);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DeviceCache 重载设备失败: {DeviceId}", deviceId);
        }
    }
}
