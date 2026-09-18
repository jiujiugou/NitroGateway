using System.Diagnostics;
using Microsoft.Extensions.Logging;
using NitroGateway.DeviceManagement;
using NitroGateway.Domain.Devices;
using NitroGateway.Telemetry;
using NitroGateway.Telemetry.Tracing;

namespace NitroGateway.Collection;

/// <summary>
/// 设备采集器实现。
/// 每轮采集：获取 Online 设备 → 熔断检查 → 并发采集每台设备（受信号量限流）。
/// 每台设备：熔断检查 → Reader → Pipeline → Dispatcher → HealthReporter。
/// <para><b>状态策略：</b>是否真正采集由熔断器（<see cref="ICircuitBreaker"/>）决定，
/// 健康判定由 HealthMonitor 负责；本类只串联流水线并上报结果。</para>
/// </summary>
internal sealed class DeviceCollector : IDeviceCollector, IDisposable
{
    private readonly IDeviceManager _deviceManager;
    private readonly IDeviceReader _reader;
    private readonly IPointValuePipeline _pipeline;
    private readonly IDataDispatcher _dispatcher;
    private readonly IHealthReporter _reporter;
    private readonly ICircuitBreakerRegistry _circuitBreakerRegistry;
    private readonly IDeviceHealthMonitor _healthMonitor;
    private readonly ISubscriptionCoordinator? _subscriptionCoordinator;
    private readonly ILogger<DeviceCollector> _logger;
    /// <summary>单轮并发限流信号量，上限由构造参数 <c>maxConcurrency</c> 决定，默认 5。</summary>
    private readonly SemaphoreSlim _concurrencyGate;

    /// <summary>创建设备采集器。</summary>
    /// <param name="deviceManager">设备目录；提供设备与点位配置</param>
    /// <param name="reader">设备数据读取器</param>
    /// <param name="pipeline">值转换管道（原始值→工程值）</param>
    /// <param name="dispatcher">数据分发（时序库双写 + 转发缓冲 + 事件）</param>
    /// <param name="reporter">健康上报</param>
    /// <param name="circuitBreakerRegistry">熔断器注册表，按设备获取/创建熔断器</param>
    /// <param name="healthMonitor">健康监控，用于维护模式过滤与状态查询</param>
    /// <param name="logger">日志记录器</param>
    /// <param name="maxConcurrency">单轮并发采集设备数上限；默认 5，必须大于 0</param>
    public DeviceCollector(
        IDeviceManager deviceManager,
        IDeviceReader reader,
        IPointValuePipeline pipeline,
        IDataDispatcher dispatcher,
        IHealthReporter reporter,
        ICircuitBreakerRegistry circuitBreakerRegistry,
        IDeviceHealthMonitor healthMonitor,
        ILogger<DeviceCollector> logger,
        int maxConcurrency = 5,
        ISubscriptionCoordinator? subscriptionCoordinator = null)
    {
        _deviceManager = deviceManager;
        _reader = reader;
        _pipeline = pipeline;
        _dispatcher = dispatcher;
        _reporter = reporter;
        _circuitBreakerRegistry = circuitBreakerRegistry;
        _healthMonitor = healthMonitor;
        _subscriptionCoordinator = subscriptionCoordinator;
        _logger = logger;
        _concurrencyGate = new SemaphoreSlim(maxConcurrency);
    }

    /// <inheritdoc />
    /// <remarks>
    /// 单台设备流程：熔断检查 → 读取 → 转换 → 分发 → 健康上报 → 熔断恢复。
    /// 读取失败会向熔断器记录失败并立即返回；读取成功（含点位质量差）只上报成功以推进探测。
    /// </remarks>
    public async Task CollectDeviceAsync(Device device, CancellationToken ct)
    {
        using var activity = GatewayActivitySource.Source.StartActivity(GatewayActivities.CollectDevice);
        activity?.SetTag(GatewayActivityTags.DeviceId, device.Id.ToString());
        activity?.SetTag(GatewayActivityTags.DeviceName, device.Name);

        _logger.LogDebug("开始采集设备 {Device}", device.Name);

        // ── 0. OPC UA Subscription（ADR-071）：活跃时由通知驱动共享管道，跳过轮询。
        //    订阅不可用/启动失败时协调器返回 false，本轮保持原轮询行为。
        if (_subscriptionCoordinator is not null && await _subscriptionCoordinator.TryActivateAsync(device, ct))
            return;

        //    全部 enabled 点未到 ScanIntervalMs → 跳过本轮：不调驱动、不触发熔断
        //    （TryEnterProbe/RecordSuccess/RecordFailure 全不碰）、不更新健康快照
        //    （保持上次状态，既不误报在线也不误判离线）。
        var duePoints = _reader.GetDuePoints(device);
        if (duePoints is { Count: 0 })
        {
            _logger.LogDebug("设备 {Device} 全部点位未到采样间隔，跳过本轮", device.Name);
            return;
        }

        // ── 熔断检查：TryEnterProbe 是命令（可能推进 Open→HalfOpen 并抢占探测名额），
        //    返回 false 表示拒绝本轮采集；诊断路径不得调用它，只能读 State ──
        var circuitBreaker = _circuitBreakerRegistry.Get(device.Id);
        if (!circuitBreaker.TryEnterProbe())
        {
            _logger.LogDebug("设备 {Device} 熔断中（{State}），跳过本轮采集",
                device.Name, circuitBreaker.State);
            return;
        }

        // RecordSuccess/RecordFailure 关闭探测；此处用 try/catch 兜底，避免未来某步骤
        // 抛异常导致 HalfOpen 探测名额被占 30 秒。
        var probeTaken = true;
        var probeReleased = false;
        try
        {
            // ── 1. 读 ──
            var readResult = await _reader.ReadDeviceAsync(device, ct);
            if (readResult.IsFailure)
            {
                _reporter.Report(device.Id, device.Name, false, readResult.Error!.Message);
                circuitBreaker.RecordFailure();
                probeReleased = true;
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
            _logger.LogDebug("转换后点位数量：{Count}", snapshots.Count);

            // ── 3. 分发 ──
            if (snapshots.Count > 0)
            {
                _logger.LogDebug("设备 {DeviceId} 开始数据分发", device.Name);
                await _dispatcher.DispatchAsync(device.Id, snapshots, ct);
            }
            else
            {
                // 属常态而非故障，降 Debug；真实读取失败仍保留 Warning。
                _logger.LogDebug("设备 {DeviceId} 没有有效点位数据，跳过分发", device.Name);
            }

            // ── 4. 健康上报 ──
            var goodCount = snapshots.Count(s => s.Quality == QualityCode.Good);

            if (snapshots.Count > 0)
                _logger.LogDebug("采集完成 {Device}: {Good}/{Total} OK, 值={Values}",
                    device.Name, goodCount, snapshots.Count,
                    string.Join(", ", snapshots.Select(s => $"{s.Value ?? s.ErrorMessage}")));

            var firstBad = snapshots.FirstOrDefault(s => s.Quality != QualityCode.Good);
            _reporter.Report(device.Id, device.Name, true, firstBad?.ErrorMessage);

            // ── 5. 熔断恢复：读成功则上报，即使部分点位质量差也不影响探测判定 ──
            circuitBreaker.RecordSuccess();
            probeReleased = true;
            NitroMetrics.CollectionTotal.WithLabels(device.Id.ToString(), "success").Inc();
            NitroMetrics.CircuitBreakerState.WithLabels(device.Id.ToString())
                .Set((int)circuitBreaker.State);
            activity?.SetStatus(ActivityStatusCode.Ok);
        }
        catch
        {
            if (probeTaken && !probeReleased)
            {
                try { circuitBreaker.RecordFailure(); } catch { /* 熔断器自身异常不阻断采集 */ }
            }
            throw;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// 设备列表来自设备目录（含 Offline/Error），实际是否采集由各设备熔断器决定；
    /// 维护模式设备由 <see cref="IsInMaintenance"/> 过滤（以 HealthMonitor 实时快照为准）。
    /// </remarks>
    public async Task CollectOnceAsync(CancellationToken ct)
    {
        _logger.LogDebug("CollectOnce 开始");
        // 获取所有设备（含 Offline）—— 熔断器决定是否实际采集
        var devicesResult = await _deviceManager.GetAllAsync(ct);
        if (devicesResult.IsFailure)
        {
            _logger.LogWarning("获取设备列表失败: {Error}", devicesResult.Error!.Message);
            return;
        }
        // 不再读设备目录缓存中的 Status（配置缓存可能滞后一个采集周期）
        var devices = devicesResult.Value!.Where(d => !IsInMaintenance(d)).ToList();
        NitroMetrics.DevicesAvailable.Set(devices.Count);
        // 与 devices_available（过滤维护模式后的待采集数）语义区分；随每轮采集刷新。
        NitroMetrics.DevicesOnline.Set(
            _healthMonitor.GetAllSnapshots().Count(s => s.Status == DeviceStatus.Online));

        if (devices.Count == 0)
        {
            _logger.LogDebug("没有设备需要采集");
            return;
        }

        _logger.LogDebug("采集轮次，共 {Count} 台设备", devices.Count);

        using var activity = GatewayActivitySource.Source.StartActivity(GatewayActivities.CollectRound);
        activity?.SetTag(GatewayActivityTags.DeviceCount, devices.Count);

        var roundStopwatch = Stopwatch.StartNew();
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
            // 正常取消
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error);
            activity?.SetTag(GatewayActivityTags.ErrorMessage, ex.ToString());
            _logger.LogError(ex, "采集过程中发生异常");
        }
        finally
        {
            NitroMetrics.CollectionDurationMs.Observe(roundStopwatch.Elapsed.TotalMilliseconds);
        }
    }

    /// <summary>
    /// 维护模式判定。优先 HealthMonitor 实时快照；设备未注册进 monitor（历史数据等）时
    /// 回退到配置中的 Status。
    /// </summary>
    private bool IsInMaintenance(Device device)
        => (_healthMonitor.GetSnapshot(device.Id)?.Status ?? device.Status) == DeviceStatus.Maintenance;

    /// <summary>释放并发闸门（Per-round scope 结束时由 DI 调用）。</summary>
    public void Dispose() => _concurrencyGate.Dispose();

}
