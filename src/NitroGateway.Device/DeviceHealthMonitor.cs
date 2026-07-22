using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using NitroGateway.DeviceManagement.Events;

namespace NitroGateway.DeviceManagement;

/// <summary>设备健康判定——唯一 SST。状态迁移时遍历 IDeviceHealthListener。</summary>
public sealed class DeviceHealthMonitor : IDeviceHealthMonitor
{
    private readonly ConcurrentDictionary<Guid, int> _failures = new();
    private readonly ConcurrentDictionary<Guid, int> _successes = new();
    private readonly ConcurrentDictionary<Guid, DeviceHealthSnapshot> _snapshots = new();
    private readonly ConcurrentBag<IDeviceHealthListener> _listeners = [];
    private readonly ILogger<DeviceHealthMonitor> _logger;

    /// <inheritdoc />
    public int FailureThreshold { get; }

    /// <inheritdoc />
    public int RecoveryThreshold { get; }

    public DeviceHealthMonitor(
        ILogger<DeviceHealthMonitor> logger,
        int failureThreshold = 3,
        int recoveryThreshold = 3)
    {
        _logger = logger;
        FailureThreshold = failureThreshold;
        RecoveryThreshold = recoveryThreshold;
    }

    // ═══════ Listener 注册 ═══════

    /// <inheritdoc />
    public void AddListener(IDeviceHealthListener listener)
    {
        _listeners.Add(listener);
    }

    // ═══════ 上报 ═══════

    /// <inheritdoc />
    public void ReportSuccess(Guid deviceId)
    {
        _failures.TryRemove(deviceId, out _);
        var count = _successes.AddOrUpdate(deviceId, 1, (_, v) => v + 1);

        UpdateSnapshot(deviceId, s => s with
        {
            ConsecutiveFailures = 0, ConsecutiveSuccesses = count,
            LastCollectionAt = DateTime.UtcNow, LastError = null
        });

        if (count == RecoveryThreshold)
        {
            _successes.TryRemove(deviceId, out _);
            var snap = GetSnapshot(deviceId);
            if (snap?.Status != Domain.Devices.DeviceStatus.Online)
            {
                _logger.LogInformation("设备 {DeviceId} 恢复 ({From}→Online)", deviceId, snap?.Status);
                NotifyListeners(deviceId, snap?.Status ?? Domain.Devices.DeviceStatus.Unknown, Domain.Devices.DeviceStatus.Online);
            }
        }
    }

    /// <inheritdoc />
    public void ReportFailure(Guid deviceId, string reason)
    {
        _successes.TryRemove(deviceId, out _);
        var count = _failures.AddOrUpdate(deviceId, 1, (_, v) => v + 1);

        UpdateSnapshot(deviceId, s => s with
        {
            ConsecutiveFailures = count, ConsecutiveSuccesses = 0,
            LastCollectionAt = DateTime.UtcNow, LastError = reason
        });

        if (count == FailureThreshold)
        {
            _failures.TryRemove(deviceId, out _);
            _logger.LogWarning("设备 {DeviceId} 连续失败 {Count} 次，触发离线", deviceId, count);
            NotifyListeners(deviceId, Domain.Devices.DeviceStatus.Online, Domain.Devices.DeviceStatus.Offline);
        }
    }

    // ═══════ 查询 ═══════

    /// <inheritdoc />
    public void UpdateStatus(Guid deviceId, Domain.Devices.DeviceStatus status)
    {
        UpdateSnapshot(deviceId, s => s with { Status = status });
    }

    /// <inheritdoc />
    public DeviceHealthSnapshot? GetSnapshot(Guid deviceId)
        => _snapshots.TryGetValue(deviceId, out var s) ? s : null;

    /// <inheritdoc />
    public IReadOnlyList<DeviceHealthSnapshot> GetAllSnapshots()
        => _snapshots.Values.ToList();

    public int GetConsecutiveFailures(Guid deviceId)
        => _failures.TryGetValue(deviceId, out var c) ? c : 0;

    public int GetConsecutiveSuccesses(Guid deviceId)
        => _successes.TryGetValue(deviceId, out var c) ? c : 0;

    // ═══════ 内部 ═══════

    private void NotifyListeners(Guid deviceId, Domain.Devices.DeviceStatus old, Domain.Devices.DeviceStatus @new)
    {
        UpdateSnapshot(deviceId, s => s with { Status = @new });

        var e = new DeviceHealthChanged { DeviceId = deviceId, OldStatus = old, NewStatus = @new };
        foreach (var listener in _listeners)
        {
            _ = listener.OnHealthChangedAsync(e); // fire-and-forget，异常不传播
        }
    }

    private void UpdateSnapshot(Guid deviceId, Func<DeviceHealthSnapshot, DeviceHealthSnapshot> update)
    {
        _snapshots.AddOrUpdate(
            deviceId,
            _ => update(new DeviceHealthSnapshot { DeviceId = deviceId, Status = Domain.Devices.DeviceStatus.Unknown }),
            (_, existing) => update(existing));
    }
}
