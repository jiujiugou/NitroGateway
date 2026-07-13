using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace NitroGateway.DeviceManagement;

/// <summary>设备健康判定实现。计数 + 阈值触发 + 快照维护</summary>
public sealed class DeviceHealthMonitor : IDeviceHealthMonitor
{
    private readonly ConcurrentDictionary<Guid, int> _failures = new();
    private readonly ConcurrentDictionary<Guid, int> _successes = new();
    private readonly ConcurrentDictionary<Guid, DeviceHealthSnapshot> _snapshots = new();
    private readonly ILogger<DeviceHealthMonitor> _logger;

    /// <inheritdoc />
    public int FailureThreshold { get; }

    /// <inheritdoc />
    public int RecoveryThreshold { get; }

    /// <inheritdoc />
    public event Action<Guid, Domain.Devices.DeviceStatus>? StatusChanged;

    public DeviceHealthMonitor(
        ILogger<DeviceHealthMonitor> logger,
        int failureThreshold = 10,
        int recoveryThreshold = 3)
    {
        _logger = logger;
        FailureThreshold = failureThreshold;
        RecoveryThreshold = recoveryThreshold;
    }

    // ═══════════════════════════════════════════════════════════════
    //  上报
    // ═══════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public void ReportSuccess(Guid deviceId)
    {
        _failures.TryRemove(deviceId, out _);
        var count = _successes.AddOrUpdate(deviceId, 1, (_, v) => v + 1);

        UpdateSnapshot(deviceId, s => s with
        {
            ConsecutiveFailures = 0,
            ConsecutiveSuccesses = count,
            LastCollectionAt = DateTime.UtcNow,
            LastError = null
        });

        if (count == RecoveryThreshold)
        {
            _successes.TryRemove(deviceId, out _);
            _logger.LogInformation("设备 {DeviceId} 连续成功 {Count} 次，触发恢复", deviceId, count);
            StatusChanged?.Invoke(deviceId, Domain.Devices.DeviceStatus.Online);
        }
    }

    /// <inheritdoc />
    public void ReportFailure(Guid deviceId, string reason)
    {
        _successes.TryRemove(deviceId, out _);
        var count = _failures.AddOrUpdate(deviceId, 1, (_, v) => v + 1);

        UpdateSnapshot(deviceId, s => s with
        {
            ConsecutiveFailures = count,
            ConsecutiveSuccesses = 0,
            LastCollectionAt = DateTime.UtcNow,
            LastError = reason
        });

        _logger.LogDebug("设备 {DeviceId} 采集失败 ({Count}/{Threshold}): {Reason}",
            deviceId, count, FailureThreshold, reason);

        if (count == FailureThreshold)
        {
            _failures.TryRemove(deviceId, out _);
            _logger.LogWarning("设备 {DeviceId} 连续失败 {Count} 次，触发离线", deviceId, count);
            StatusChanged?.Invoke(deviceId, Domain.Devices.DeviceStatus.Offline);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  查询
    // ═══════════════════════════════════════════════════════════════

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

    /// <inheritdoc />
    public int GetConsecutiveFailures(Guid deviceId)
        => _failures.TryGetValue(deviceId, out var c) ? c : 0;

    /// <inheritdoc />
    public int GetConsecutiveSuccesses(Guid deviceId)
        => _successes.TryGetValue(deviceId, out var c) ? c : 0;

    // ═══════════════════════════════════════════════════════════════
    //  内部
    // ═══════════════════════════════════════════════════════════════

    private void UpdateSnapshot(Guid deviceId, Func<DeviceHealthSnapshot, DeviceHealthSnapshot> update)
    {
        _snapshots.AddOrUpdate(
            deviceId,
            _ => update(new DeviceHealthSnapshot
            {
                DeviceId = deviceId,
                Status = Domain.Devices.DeviceStatus.Unknown,
                ConsecutiveFailures = 0,
                ConsecutiveSuccesses = 0
            }),
            (_, existing) => update(existing));
    }
}
