using Microsoft.Extensions.Logging;
using NitroGateway.DeviceManagement.Events;
using NitroGateway.Domain.Devices;
using System.Collections.Concurrent;

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
    public void ReportSuccess(Guid deviceId, string? deviceName)
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
                _logger.LogInformation("设备 {DeviceName} [{DeviceId}] 恢复 ({From}→Online)",
                    deviceName, deviceId, snap?.Status);
                NotifyListeners(deviceId, deviceName,
                    snap?.Status ?? Domain.Devices.DeviceStatus.Unknown, Domain.Devices.DeviceStatus.Online);
            }
        }
    }

    /// <inheritdoc />
    public void ReportFailure(Guid deviceId, string? deviceName, string reason)
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

            var snap = GetSnapshot(deviceId);

            if (snap?.Status != DeviceStatus.Offline)
            {
                _logger.LogWarning(
                    "设备 {DeviceName} [{DeviceId}] 连续失败 {Count} 次，触发离线",
                    deviceName,
                    deviceId,
                    count);

                NotifyListeners(
                    deviceId,
                    deviceName,
                    snap?.Status ?? DeviceStatus.Unknown,
                    DeviceStatus.Offline);
            }
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

    /// <inheritdoc />
    public void Remove(Guid deviceId)
    {
        _failures.TryRemove(deviceId, out _);
        _successes.TryRemove(deviceId, out _);
        _snapshots.TryRemove(deviceId, out _);
    }

    public int GetConsecutiveFailures(Guid deviceId)
        => _failures.TryGetValue(deviceId, out var c) ? c : 0;

    public int GetConsecutiveSuccesses(Guid deviceId)
        => _successes.TryGetValue(deviceId, out var c) ? c : 0;

    // ═══════ 内部 ═══════

    private void NotifyListeners(Guid deviceId, string? deviceName, Domain.Devices.DeviceStatus old, Domain.Devices.DeviceStatus @new)
    {
        // ADR-030 L1：监听器数量是诊断信息，健康变更（恢复/离线）日志已分别记录，此处降 Debug 避免每次变更刷屏
        _logger.LogDebug("HealthListener 数量: {Count}", _listeners.Count);
        UpdateSnapshot(deviceId, s => s with { Status = @new });

        var e = new DeviceHealthChanged { DeviceId = deviceId, DeviceName = deviceName, OldStatus = old, NewStatus = @new };
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
