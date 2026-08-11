using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NitroGateway.Host;
using NitroGateway.Storage.Buffer;
using NitroGateway.Storage.Disk;
using NitroGateway.Transport.MQTT;

namespace NitroGateway.Forwarder;

/// <summary>
/// 转发引擎。BackgroundService + PeriodicTimer，定时触发转发。
/// <para>行为要点：</para>
/// <list type="bullet">
/// <item>首轮立即执行（不等第一个周期，ADR-001 P3-12），之后每 <see cref="_interval"/> 触发一轮；</item>
/// <item>每轮先查积压并限流告警（首次超限立即、之后每 <see cref="BacklogWarningInterval"/> 一次，回落后重置）；</item>
/// <item>MQTT 未连接时跳过本轮，避免空转出队；已连接时单轮最多排水 <see cref="MaxDrainPerRound"/> 批，超出部分下轮继续；</item>
/// <item>Host 停止时由 stoppingToken 取消等待，随后执行停机排空（ADR-016 P1-1）：等待采集侧
/// （<see cref="GatewayLifecycle.IsStopped"/>）完成后，在 MQTT 仍连接期间把缓冲剩余批次尽量发完。</item>
/// </list>
/// </summary>
public sealed class ForwarderEngine : BackgroundService
{
    /// <summary>积压告警阈值（批）：缓冲区待转发批次数超过此值时记录 Warning 级日志</summary>
    private const int BacklogWarningThreshold = 1000;

    /// <summary>积压告警最小间隔（ADR-001 P2-8）：首次超限立即告警，之后每 60s 一次，防止长断线期间每轮刷屏</summary>
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

    /// <summary>磁盘状态（ADR-012）：Critical 时暂停出队，保护磁盘；null 表示不启用降级（独立测试用）</summary>
    private readonly IDiskStatus? _diskStatus;

    /// <summary>网关生命周期：停机排空时等待采集侧完成最后一轮（ADR-016 P1-1）。</summary>
    private readonly GatewayLifecycle _lifecycle;

    /// <summary>停机排空：等待采集侧停止的超时上限。</summary>
    private static readonly TimeSpan DrainWaitCollectionTimeout = TimeSpan.FromSeconds(15);

    /// <summary>停机排空：排空剩余缓冲的时间上限，防止停机被慢 Broker 拖死。</summary>
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(10);

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

    /// <summary>
    /// 引擎主循环：创建 PeriodicTimer 后立即执行首轮（ADR-001 P3-12），随后按间隔循环执行。
    /// </summary>
    /// <param name="stoppingToken">Host 停机令牌；取消后 WaitForNextTickAsync 抛 OperationCanceledException，
    /// 此处捕获并正常结束循环</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 启动信号：与末尾 "ForwarderEngine Stopped." 配对，便于运维确认引擎生命周期；
        // 同时是测试确定引擎真正开始执行（而非 Task.Run 排队中）的握手点（ADR-028 P1-1）。
        _logger.LogInformation("ForwarderEngine Started.");

        using var timer = new PeriodicTimer(_interval);

        try
        {
            // ADR-001 P3-12：首轮立即执行，不等第一个周期 tick，避免启动后空等一个周期
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

        // ADR-016 P1-1：停机排空——采集侧停止后，把缓冲剩余批次尽量发完再退出（MQTT 此时仍连接）
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

        var drainDeadline = DateTime.UtcNow + DrainTimeout;
        while (DateTime.UtcNow < drainDeadline)
        {
            try
            {
                var pending = await _buffer.GetCountAsync(CancellationToken.None);
                if (pending == 0)
                    break;

                using var scope = _scopeFactory.CreateScope();
                var mqtt = scope.ServiceProvider.GetRequiredService<IMqttClient>();
                if (mqtt.State != MqttConnectionState.Connected)
                    break; // MQTT 已不可用：剩余批次留在缓冲，下次启动续传

                var forwarder = scope.ServiceProvider.GetRequiredService<IForwarder>();
                await forwarder.ForwardBatchAsync(MaxDrainPerRound, CancellationToken.None);
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
        // ADR-012 P3：磁盘 Critical 降级——跳过本轮出队，剩余批次留在缓冲等磁盘恢复后续传
        if (_diskStatus?.Level == DiskLevel.Critical)
            return;

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
            // ADR-017 P1-1：缓冲查询异常不能放倒引擎（BackgroundService 未捕获异常默认 StopHost）——
            // 记 Error 跳过本轮，下轮重试；GetCountAsync 自身也已按 0 处理，此处是接口级兜底。
            _logger.LogError(ex, "转发积压查询异常，跳过本轮");
            return;
        }
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
