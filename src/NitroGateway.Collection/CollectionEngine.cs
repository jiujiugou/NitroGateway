using Microsoft.Extensions.Logging;
using NitroGateway.DeviceManagement;
using NitroGateway.Domain.Devices;
using DomainDevice = NitroGateway.Domain.Devices.Device;

namespace NitroGateway.Collection;

/// <summary>采集引擎。串联 DeviceReader → Pipeline → Dispatcher → HealthReporter</summary>
public sealed class CollectionEngine
{
    private readonly IDeviceManager _deviceManager;
    private readonly IDeviceReader _reader;
    private readonly IPointValuePipeline _pipeline;
    private readonly IDataDispatcher _dispatcher;
    private readonly IHealthReporter _reporter;
    private readonly ILogger<CollectionEngine> _logger;

    /// <summary>创建采集引擎</summary>
    public CollectionEngine(
        IDeviceManager deviceManager,
        IDeviceReader reader,
        IPointValuePipeline pipeline,
        IDataDispatcher dispatcher,
        IHealthReporter reporter,
        ILogger<CollectionEngine> logger)
    {
        _deviceManager = deviceManager;
        _reader = reader;
        _pipeline = pipeline;
        _dispatcher = dispatcher;
        _reporter = reporter;
        _logger = logger;
    }

    /// <summary>对一台设备执行一轮完整采集</summary>
    public async Task CollectDeviceAsync(Guid deviceId, CancellationToken ct)
    {
        var deviceResult = await _deviceManager.GetAsync(deviceId, ct);
        if (deviceResult.IsFailure)
        {
            _logger.LogWarning("获取设备失败 {DeviceId}: {Error}", deviceId, deviceResult.Error!.Message);
            return;
        }

        var device = deviceResult.Value!;
        if (device.Points.All(p => !p.Enabled))
            return;

        // 1. 读
        var readResult = await _reader.ReadDeviceAsync(device, ct);
        if (readResult.IsFailure)
        {
            _reporter.Report(deviceId, 0, 1, readResult.Error!.Message);
            _logger.LogWarning("设备 {DeviceId} 读取失败: {Error}", deviceId, readResult.Error!.Message);
            return;
        }

        // 2. 转换
        var snapshots = _pipeline.Process(deviceId, readResult.Value!);

        // 3. 分发
        if (snapshots.Count > 0)
            await _dispatcher.DispatchAsync(deviceId, snapshots, ct);

        // 4. 健康上报
        var successCount = snapshots.Count(s => s.Quality == QualityCode.Good);
        var failCount = snapshots.Count - successCount;

        if (snapshots.Count > 0)
            _logger.LogInformation("采集完成: {Success}/{Total} OK, 值={Values}",
                successCount, snapshots.Count,
                string.Join(", ", snapshots.Select(s => $"{s.Value ?? s.ErrorMessage}")));

        _reporter.Report(deviceId, successCount, failCount, null);
    }

    /// <summary>对所有 Online 设备执行一轮采集。由 Scheduler 定时调用</summary>
    public async Task CollectAllOnlineAsync(CancellationToken ct = default)
    {
        var devicesResult = await _deviceManager.GetByStatusAsync(DeviceStatus.Online, ct);
        if (devicesResult.IsFailure) return;

        foreach (var device in devicesResult.Value!)
            await CollectDeviceAsync(device.Id, ct);
    }
}
