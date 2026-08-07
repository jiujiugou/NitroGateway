using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NitroGateway.Domain.Devices;
using NitroGateway.Shared;
using NitroGateway.Storage.Configuration;

namespace NitroGateway.DeviceManagement;

/// <summary>
/// <inheritdoc cref="IDeviceSnapshotCache"/>
/// 缓存内容为设备+点位配置快照；调用方如需最新运行状态，应改查 <see cref="IDeviceHealthMonitor"/>。
/// </summary>
public sealed class DeviceSnapshotCache : IDeviceSnapshotCache
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DeviceSnapshotCache> _logger;
    private readonly TimeSpan _ttl;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private IReadOnlyList<Device>? _snapshot;
    private DateTimeOffset _loadedAt;
    private bool _invalidated = true;

    /// <param name="ttl">无失效事件时的最大缓存时长，默认 10 秒（配置写入均会主动 Invalidate）</param>
    public DeviceSnapshotCache(IServiceScopeFactory scopeFactory, ILogger<DeviceSnapshotCache> logger, TimeSpan? ttl = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _ttl = ttl ?? TimeSpan.FromSeconds(10);
    }

    /// <inheritdoc />
    public async Task<OperationResult<IReadOnlyList<Device>>> GetAllAsync(CancellationToken ct = default)
    {
        if (IsFresh())
            return OperationResult<IReadOnlyList<Device>>.Success(_snapshot!);

        await _gate.WaitAsync(ct);
        try
        {
            // 双检：等待期间可能已被其他线程刷新
            if (IsFresh())
                return OperationResult<IReadOnlyList<Device>>.Success(_snapshot!);

            using var scope = _scopeFactory.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IDeviceRepository>();
            var result = await repository.GetAllAsync(ct);
            if (result.IsFailure)
                return result.Error!;

            _snapshot = result.Value;
            _loadedAt = DateTimeOffset.UtcNow;
            _invalidated = false;
            _logger.LogDebug("设备目录缓存已刷新：{Count} 台设备", _snapshot!.Count);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Invalidate() => _invalidated = true;

    private bool IsFresh()
        => !_invalidated && _snapshot is not null && DateTimeOffset.UtcNow - _loadedAt < _ttl;
}
