using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NitroGateway.Host;
using NitroGateway.Storage.Buffer;
using NitroGateway.Storage.Disk;
using NitroGateway.Telemetry;
using NitroGateway.Transport.MQTT;

namespace NitroGateway.Forwarder;

public sealed class ForwarderEngine : BackgroundService
{
    /// <summary>积压告警阈值（批）：缓冲区待转发批次数超过此值时记录 Warning 级日志</summary>
    private const int BacklogWarningThreshold = 1000;

    private static readonly TimeSpan BacklogWarningInterval = TimeSpan.FromSeconds(60);

    /// <summary>单轮最大排水量（批）：MQTT 恢复瞬间限流，防止冲垮 Broker，超出部分留待下轮继续</summary>
    private const int MaxDrainPerRound = 2000;

    /// <summary>上次积压告警时间（UTC）；积压回落后重置为 MinValue，保证下次超限立即再告警</summary>
    private DateTimeOffset _lastBacklogWarningAt = DateTimeOffset.MinValue;

    /// <summary>每轮创建独立 DI 作用域，从中解析 IMqttClient 与 IForwarder</summary>
    private readonly IServiceScopeFactory _scopeFactory;

    /// <summary>轮询周期：相邻两轮触发间隔（由 AddNitroForwarder 的 intervalMs 配置）</summary>
    private readonly TimeSpan _interval;

    /// <summary>转发缓冲：每轮查询待转发批次数，用于积压告警判断</summary>
    private readonly IForwardBuffer _buffer;

    /// <summary>日志</summary>
    private readonly ILogger<ForwarderEngine> _logger;

    private readonly IDiskStatus? _diskStatus;

    private readonly GatewayLifecycle _lifecycle;

    /// <summary>停机排空：等待采集侧停止的超时上限（关停必须快，不能被子系统拖住）。</summary>
    private static readonly TimeSpan DrainWaitCollectionTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// 停机排空：整段排空的时间上限，防止停机被慢 Broker 拖死。
    /// 该预算作为取消令牌传入 <see cref="IForwarder.ForwardBatchAsync"/>，保证**单次**调用也受约束
    /// （单次真发 1000 批到高 RTT 远程 Broker 可能耗时数十秒）。
    /// </summary>
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(2);

    /// <summary>创建转发引擎</summary>
    /// <param name="scopeFactory">DI 作用域工厂，每轮创建作用域解析转发所需服务</param>
    /// <param name="interval">轮询周期；必须为正数（PeriodicTimer 要求），如 5 秒</param>
    /// <param name="buffer">转发缓冲：查询积压批次数</param>
    /// <param name="logger">日志</param>
    /// <param name="lifecycle">网关生命周期；缺省时使用独立实例（无采集侧时不停机等待，便于独立测试）</param>
    public ForwarderEngine(
        IServiceScopeFactory scopeFactory,
        TimeSpan interval,
        IForwardBuffer buffer,
        ILogger<ForwarderEngine> logger,
        GatewayLifecycle? lifecycle = null,
        IDiskStatus? diskStatus = null)
    {
        _scopeFactory = scopeFactory;
        _interval = interval;
        _buffer = buffer;
        _logger = logger;
        _lifecycle = lifecycle ?? new GatewayLifecycle();
        _diskStatus = diskStatus;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 启动信号：与末尾 "ForwarderEngine Stopped." 配对，便于运维确认引擎生命周期；
        _logger.LogInformation("ForwarderEngine Started.");

        using var timer = new PeriodicTimer(_interval);

        try
        {
            do
            {
                await RunRoundAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host 正常停止
        }

        if (stoppingToken.IsCancellationRequested)
            await DrainOnShutdownAsync();

        _logger.LogInformation("ForwarderEngine Stopped.");
    }

    /// <summary>
    /// 停机排空：等待采集侧完成最后一轮（把最新数据入缓冲），然后限时把缓冲排空。
    /// 仅在采集侧确在停止（<see cref="GatewayLifecycle.IsDraining"/>）时才等待；独立运行（无采集侧）时直接排空。
    /// </summary>
    private async Task DrainOnShutdownAsync()
    {
        if (_lifecycle.IsDraining && !_lifecycle.IsStopped)
        {
            var waitDeadline = DateTime.UtcNow + DrainWaitCollectionTimeout;
            while (!_lifecycle.IsStopped && DateTime.UtcNow < waitDeadline)
                await Task.Delay(100);

            if (!_lifecycle.IsStopped)
                _logger.LogWarning("停机排空：等待采集停止超时，按当前缓冲内容排空");
        }

        // 整段排空用带超时的取消令牌；透传给 ForwardBatchAsync 使**单次**调用也在预算内被取消，
        // 否则一次真发 1000 批到远程 Broker 会把关停拖到分钟级（进程残留、独占单实例锁）。
        using var drainCts = new CancellationTokenSource(DrainTimeout);
        var drainToken = drainCts.Token;
        while (!drainToken.IsCancellationRequested)
        {
            try
            {
                var pending = await _buffer.GetCountAsync(drainToken);
                if (pending == 0)
                    break;

                using var scope = _scopeFactory.CreateScope();
                var mqtt = scope.ServiceProvider.GetRequiredService<IMqttClient>();
                if (mqtt.State != MqttConnectionState.Connected)
                    break; // MQTT 已不可用：剩余批次留在缓冲，下次启动续传

                var forwarder = scope.ServiceProvider.GetRequiredService<IForwarder>();
                await forwarder.ForwardBatchAsync(MaxDrainPerRound, drainToken);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("停机排空到达时间上限，剩余批次留待下次启动续传");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "停机排空异常，结束排空");
                break;
            }
        }
    }

    /// <summary>
    /// 执行一轮转发：积压告警检查 → 解析服务 → 排水（MQTT 未连接则跳过本轮）。
    /// 单轮异常（非取消）记录 Error 日志后继续下一轮，保证引擎不因单轮故障退出。
    /// </summary>
    /// <param name="stoppingToken">取消令牌，透传给缓冲查询与转发调用</param>
    private async Task RunRoundAsync(CancellationToken stoppingToken)
    {
        if (_diskStatus?.Level == DiskLevel.Critical)
        {
            NitroMetrics.DispatchSkippedTotal.WithLabels("disk_critical").Inc();
            return;
        }

        // ── 积压检查（限流：首次立即 + 之后每 60s 一次，回落后重置）──
        int backlog;
        try
        {
            backlog = await _buffer.GetCountAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            throw; // 停机取消：交给 ExecuteAsync 的停机路径
        }
        catch (Exception ex)
        {
            // 记 Error 跳过本轮，下轮重试；GetCountAsync 自身也已按 0 处理，此处是接口级兜底。
            _logger.LogError(ex, "转发积压查询异常，跳过本轮");
            return;
        }

        // 无论 MQTT 是否连接都刷新积压指标：否则未连接时 Forwarder 不运行，
        // nitro_buffer_backlog 会恒为初值 0，掩盖"缓冲已满/数据被拒"的真实状态。
        NitroMetrics.BufferBacklog.Set(backlog);
        NitroMetrics.BufferBacklogByChannel
            .WithLabels(IForwardBuffer.MqttChannel)
            .Set(await _buffer.GetCountAsync(IForwardBuffer.MqttChannel, stoppingToken));

        if (backlog > BacklogWarningThreshold)
        {
            var now = DateTimeOffset.UtcNow;
            if (_lastBacklogWarningAt == DateTimeOffset.MinValue ||
                now - _lastBacklogWarningAt >= BacklogWarningInterval)
            {
                _logger.LogWarning(
                    "转发缓冲区积压过高: {Count} 批（阈值 {Threshold}），MQTT 恢复后 throttled drain 将分批排水",
                    backlog, BacklogWarningThreshold);
                _lastBacklogWarningAt = now;
            }
        }
        else
        {
            // 积压回落：重置限流状态，下次超限立即再告警
            _lastBacklogWarningAt = DateTimeOffset.MinValue;
        }

        using var scope = _scopeFactory.CreateScope();
        var mqtt = scope.ServiceProvider.GetRequiredService<IMqttClient>();

        // MQTT 未连接，跳过本轮
        if (mqtt.State != MqttConnectionState.Connected)
            return;

        var forwarder = scope.ServiceProvider.GetRequiredService<IForwarder>();

        try
        {
            // 限制单轮排水量，超出部分下轮继续
            await forwarder.ForwardBatchAsync(MaxDrainPerRound, stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "转发循环发生异常");
        }
    }
}
