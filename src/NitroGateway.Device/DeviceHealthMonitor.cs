using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace NitroGateway.DeviceManagement;

/// <summary>设备健康判定实现。计数 + 阈值触发</summary>
public sealed class DeviceHealthMonitor : IDeviceHealthMonitor
{
    private readonly ConcurrentDictionary<Guid, int> _failures = new();
    private readonly ConcurrentDictionary<Guid, int> _successes = new();
    private readonly ILogger<DeviceHealthMonitor> _logger;

    public int FailureThreshold { get; } = 10;
    public int RecoveryThreshold { get; } = 3;

    public event Action<Guid, Domain.Devices.DeviceStatus>? ThresholdReached;

    public DeviceHealthMonitor(ILogger<DeviceHealthMonitor> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public void ReportSuccess(Guid deviceId)
    {
        _failures.TryRemove(deviceId, out _);
        var count = _successes.AddOrUpdate(deviceId, 1, (_, v) => v + 1);

        if (count == RecoveryThreshold)
        {
            _successes.TryRemove(deviceId, out _);
            _logger.LogInformation("设备 {DeviceId} 连续成功 {Count} 次，触发恢复", deviceId, count);
            ThresholdReached?.Invoke(deviceId, Domain.Devices.DeviceStatus.Online);
        }
    }

    /// <inheritdoc />
    public void ReportFailure(Guid deviceId, string reason)
    {
        _successes.TryRemove(deviceId, out _);
        var count = _failures.AddOrUpdate(deviceId, 1, (_, v) => v + 1);

        _logger.LogDebug("设备 {DeviceId} 采集失败 ({Count}/{Threshold}): {Reason}",
            deviceId, count, FailureThreshold, reason);

        if (count == FailureThreshold)
        {
            _failures.TryRemove(deviceId, out _);
            _logger.LogWarning("设备 {DeviceId} 连续失败 {Count} 次，触发离线", deviceId, count);
            ThresholdReached?.Invoke(deviceId, Domain.Devices.DeviceStatus.Offline);
        }
    }

    /// <summary>获取设备当前连续失败次数</summary>
    public int GetConsecutiveFailures(Guid deviceId)
        => _failures.TryGetValue(deviceId, out var c) ? c : 0;

    /// <summary>获取设备当前连续成功次数</summary>
    public int GetConsecutiveSuccesses(Guid deviceId)
        => _successes.TryGetValue(deviceId, out var c) ? c : 0;
}
