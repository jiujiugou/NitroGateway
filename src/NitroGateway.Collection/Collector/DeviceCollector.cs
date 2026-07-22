using System.Diagnostics;
using Microsoft.Extensions.Logging;
using NitroGateway.DeviceManagement;
using NitroGateway.Domain.Devices;
using NitroGateway.Telemetry;
using NitroGateway.Telemetry.Tracing;

namespace NitroGateway.Collection;

/// <summary>
/// 设备采集器实现。
/// 每轮采集：获取 Online 设备 → 并发采集每台设备（受信号量限流）。
/// 每台设备：熔断检查 → Reader → Pipeline → Dispatcher → HealthReporter。
/// </summary>
internal sealed class DeviceCollector : IDeviceCollector
{
    private readonly IDeviceManager _deviceManager;
    private readonly IDeviceReader _reader;
    private readonly IPointValuePipeline _pipeline;
    private readonly IDataDispatcher _dispatcher;
    private readonly IHealthReporter _reporter;
    private readonly ICircuitBreakerRegistry _circuitBreakerRegistry;
    private readonly ILogger<CollectionEngine> _logger;
    private readonly SemaphoreSlim _concurrencyGate;

    /// <summary>创建设备采集器</summary>
    public DeviceCollector(
        IDeviceManager deviceManager,
        IDeviceReader reader,
        IPointValuePipeline pipeline,
        IDataDispatcher dispatcher,
        IHealthReporter reporter,
        ICircuitBreakerRegistry circuitBreakerRegistry,
        ILogger<CollectionEngine> logger,
        int maxConcurrency = 5)
    {
        _deviceManager = deviceManager;
        _reader = reader;
        _pipeline = pipeline;
        _dispatcher = dispatcher;
        _reporter = reporter;
        _circuitBreakerRegistry = circuitBreakerRegistry;
        _logger = logger;
        _concurrencyGate = new SemaphoreSlim(maxConcurrency);
    }

    /// <inheritdoc />
    public async Task CollectDeviceAsync(Device device, CancellationToken ct)
    {
        using var activity = GatewayActivitySource.Source.StartActivity(GatewayActivities.CollectDevice);
        activity?.SetTag(GatewayActivityTags.DeviceId, device.Id.ToString());
        activity?.SetTag(GatewayActivityTags.DeviceName, device.Name);

        var circuitBreaker = _circuitBreakerRegistry.Get(device.Id);

        // ── 熔断检查 ──
        if (circuitBreaker.IsOpen)
        {
            activity?.SetStatus(ActivityStatusCode.Ok);
            _logger.LogDebug("设备 {DeviceId} 处于熔断状态（{State}），跳过本轮采集",
                device.Name, circuitBreaker.State);
            return;
        }

        // 设备对象来自 DeviceCache，已包含完整 Points——无需再查 DB
        if (device.Points.All(p => !p.Enabled))
        {
            activity?.SetStatus(ActivityStatusCode.Ok);
            return;
        }
        _logger.LogDebug("开始采集设备 {Device}",device.Name);
        // ── 1. 读 ──
        var readResult = await _reader.ReadDeviceAsync(device, ct);
        if (readResult.IsFailure)
        {
            _reporter.Report(device.Id, 0, 1, readResult.Error!.Message);
            circuitBreaker.RecordFailure();
            NitroMetrics.CollectionTotal.WithLabels(device.Id.ToString(), "failure").Inc();
            NitroMetrics.CircuitBreakerState.WithLabels(device.Id.ToString())
                .Set((int)circuitBreaker.State);
            activity?.SetStatus(ActivityStatusCode.Error);
            activity?.SetTag(GatewayActivityTags.ErrorMessage, readResult.Error!.Message);
            _logger.LogWarning("设备 {DeviceId} 读取失败: {Error}", device.Name, readResult.Error!.Message);
            return;
        }
        _logger.LogDebug("原始点位数量：{Count}", readResult.Value!.Count);
        // ── 2. 转换 ──
        var snapshots = _pipeline.Process(device.Id, readResult.Value!);
        //_logger.LogInformation("设备 {DeviceId} 采集完成，{Count} 个点位", device.Name, snapshots.Count);
        _logger.LogDebug("转换后点位数量：{Count}", snapshots.Count);
        // ── 3. 分发 ──
        if (snapshots.Count > 0)
        {
            _logger.LogDebug("设备 {DeviceId} 开始数据分发",device.Name);
            await _dispatcher.DispatchAsync(device.Id, snapshots, ct);
        }
        else
        {
            _logger.LogWarning("设备 {DeviceId} 没有有效点位数据，跳过分发", device.Name);
        }

        // ── 4. 健康上报 ──
        var goodCount = snapshots.Count(s => s.Quality == QualityCode.Good);
        var failCount = snapshots.Count - goodCount;

        if (snapshots.Count > 0)
            _logger.LogInformation("采集完成 {Device}: {Good}/{Total} OK, 值={Values}",
                device.Name, goodCount, snapshots.Count,
                string.Join(", ", snapshots.Select(s => $"{s.Value ?? s.ErrorMessage}")));

        _reporter.Report(device.Id, goodCount, failCount, null);

        // ── 5. 熔断恢复 ── 读成功就上报，即使部分点位质量差也不影响
        circuitBreaker.RecordSuccess();
        NitroMetrics.CollectionTotal.WithLabels(device.Id.ToString(), "success").Inc();
        NitroMetrics.CircuitBreakerState.WithLabels(device.Id.ToString())
            .Set((int)circuitBreaker.State);
        activity?.SetStatus(ActivityStatusCode.Ok);
    }

    /// <inheritdoc />
    public async Task CollectOnceAsync(CancellationToken ct)
    {
        _logger.LogDebug("CollectOnce 开始");
        // 获取所有设备（含 Offline）——熔断器负责决定是否实际采集
        var devicesResult = await _deviceManager.GetAllAsync(ct);
        if (devicesResult.IsFailure)
        {
            _logger.LogWarning("获取设备列表失败: {Error}", devicesResult.Error!.Message);
            return;
        }
        var devices = devicesResult.Value!.Where(d => d.Status != DeviceStatus.Maintenance).ToList();
        NitroMetrics.DevicesAvailable.Set(devices.Count);

        if (devices.Count == 0)
        {
            _logger.LogDebug("没有设备需要采集");
            return;
        }

        _logger.LogDebug("采集轮次，共 {Count} 台设备", devices.Count);

        using var activity = GatewayActivitySource.Source.StartActivity(GatewayActivities.CollectRound);
        activity?.SetTag(GatewayActivityTags.DeviceCount, devices.Count);

        try
        {
            ct.ThrowIfCancellationRequested();

            // 受并发限制的并行采集
            var tasks = devices.Select(async device =>
            {
                await _concurrencyGate.WaitAsync(ct);
                try
                {
                    await CollectDeviceAsync(device, ct);
                }
                finally
                {
                    _concurrencyGate.Release();
                }
            });

            await Task.WhenAll(tasks);
            activity?.SetStatus(ActivityStatusCode.Ok);
            _logger.LogDebug("CollectOnce 结束");
        }
        catch (OperationCanceledException)
        {
            activity?.SetStatus(ActivityStatusCode.Error);
            // 正常取消，不记日志
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error);
            activity?.SetTag(GatewayActivityTags.ErrorMessage, ex.ToString());
            _logger.LogError(ex, "采集过程中发生异常");
        }
    }
}
