using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NitroGateway.Domain.Devices;
using NitroGateway.Primitives.Caching;
using NitroGateway.Shared;
using NitroGateway.Storage.Configuration;

namespace NitroGateway.DeviceManagement;

/// <summary>
/// <inheritdoc cref="IDeviceSnapshotCache"/>
/// 缓存内容为设备+点位配置快照；调用方如需最新运行状态，应改查 <see cref="IDeviceHealthMonitor"/>。
/// TTL/闸门/双检/失效的**机制**由 <see cref="TtlCache{TKey,TValue}"/> 统一承载，
/// 本类只保留领域策略（TTL 10s、加载委托、写后失效）。
/// </summary>
public sealed class DeviceSnapshotCache : IDeviceSnapshotCache, IDisposable
{
    /// <summary>单值缓存的固定键（设备全量为唯一缓存对象）。</summary>
    private const string CacheKey = "all";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DeviceSnapshotCache> _logger;
    private readonly TtlCache<string, IReadOnlyList<Device>> _cache;

    /// <param name="ttl">无失效事件时的最大缓存时长，默认 10 秒（配置写入均会主动 Invalidate）</param>
    public DeviceSnapshotCache(IServiceScopeFactory scopeFactory, ILogger<DeviceSnapshotCache> logger, TimeSpan? ttl = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _cache = new TtlCache<string, IReadOnlyList<Device>>(ttl ?? TimeSpan.FromSeconds(10));
    }

    /// <inheritdoc />
    public Task<OperationResult<IReadOnlyList<Device>>> GetAllAsync(CancellationToken ct = default)
        => _cache.GetOrLoadAsync(CacheKey, LoadAsync, ct);

    /// <inheritdoc />
    public void Invalidate() => _cache.Invalidate();

    /// <summary>加载委托：创建 scope 取内层仓储并读取设备全量；成功后记录刷新日志。</summary>
    private async Task<OperationResult<IReadOnlyList<Device>>> LoadAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IDeviceRepository>();
        var result = await repository.GetAllAsync(ct);
        if (result.IsSuccess)
            _logger.LogDebug("设备目录缓存已刷新：{Count} 台设备", result.Value!.Count);

        return result;
    }

    /// <summary>释放刷新闸门（Singleton，宿主关闭时调用）。</summary>
    public void Dispose() => _cache.Dispose();
}
