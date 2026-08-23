using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NitroGateway.Shared;
using NitroGateway.Storage.Buffer;
using NitroGateway.Telemetry;
using NitroGateway.Transport.HTTP;

namespace NitroGateway.Forwarder;

/// <summary>
/// HTTP 北向转发引擎（ADR-011 P2）：MQTT 之外的第二条北向通道。
/// 复用 ForwarderEngine 骨架（PeriodicTimer 周期 + 批量出队 + Commit/MarkFailed + 重试超限丢弃），
/// 但按 <see cref="IForwardBuffer.HttpChannel"/> 出队并经 <see cref="IHttpClient.UploadAsync{T}"/> 逐批 POST。
/// <para><b>与 MQTT 引擎的关系：</b>Channels=both 时两引擎共享缓冲但按通道隔离出队，互不争抢；
/// 单批上传失败 MarkFailed（重试/丢弃语义与 MQTT 一致），batchId 作为服务端幂等键（ADR-020 P2-2 注释）。</para>
/// </summary>
public sealed class HttpForwarderEngine : BackgroundService
{
    /// <summary>单轮最大排水量（批）：与 MQTT 引擎一致，超出部分留待下轮继续</summary>
    private const int MaxDrainPerRound = 2000;

    /// <summary>积压告警阈值（批）：与 MQTT 引擎一致</summary>
    private const int BacklogWarningThreshold = 1000;

    /// <summary>积压告警最小间隔：首次超限立即，之后每 60s 一次，防止长断线刷屏</summary>
    private static readonly TimeSpan BacklogWarningInterval = TimeSpan.FromSeconds(60);

    /// <summary>停机排空时间上限，防止停机被慢云端拖死</summary>
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(10);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeSpan _interval;
    private readonly IForwardBuffer _buffer;
    private readonly string _path;
    private readonly ILogger<HttpForwarderEngine> _logger;

    private DateTimeOffset _lastBacklogWarningAt = DateTimeOffset.MinValue;

    /// <summary>创建 HTTP 转发引擎</summary>
    /// <param name="scopeFactory">DI 作用域工厂，每轮解析 IHttpClient</param>
    /// <param name="interval">轮询周期，必须为正数</param>
    /// <param name="buffer">转发缓冲（HttpChannel 出队）</param>
    /// <param name="path">批次上传路径</param>
    /// <param name="logger">日志</param>
    public HttpForwarderEngine(
        IServiceScopeFactory scopeFactory,
        TimeSpan interval,
        IForwardBuffer buffer,
        string path,
        ILogger<HttpForwarderEngine> logger)
    {
        _scopeFactory = scopeFactory;
        _interval = interval;
        _buffer = buffer;
        _path = path;
        _logger = logger;
    }

    /// <summary>主循环：首轮立即执行，之后按间隔循环；停机时排空剩余 http 批次。</summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("HttpForwarderEngine Started.");

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
            // 正常停止
        }

        if (stoppingToken.IsCancellationRequested)
            await DrainOnShutdownAsync();

        _logger.LogInformation("HttpForwarderEngine Stopped.");
    }

    /// <summary>执行一轮：积压告警 → 出队 http 通道 → 逐批 POST → Commit/MarkFailed。</summary>
    private async Task RunRoundAsync(CancellationToken stoppingToken)
    {
        // ── 积压检查（限流：首次立即 + 之后每 60s 一次，回落后重置）──
        int backlog;
        try
        {
            backlog = await _buffer.GetCountAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "HTTP 转发积压查询异常，跳过本轮");
            return;
        }
        if (backlog > BacklogWarningThreshold)
        {
            var now = DateTimeOffset.UtcNow;
            if (_lastBacklogWarningAt == DateTimeOffset.MinValue ||
                now - _lastBacklogWarningAt >= BacklogWarningInterval)
            {
                _logger.LogWarning(
                    "转发缓冲区积压过高: {Count} 批（阈值 {Threshold}），HTTP 通道将分批排水",
                    backlog, BacklogWarningThreshold);
                _lastBacklogWarningAt = now;
            }
        }
        else
        {
            _lastBacklogWarningAt = DateTimeOffset.MinValue;
        }

        using var scope = _scopeFactory.CreateScope();
        var http = scope.ServiceProvider.GetRequiredService<IHttpClient>();

        // 未连接/故障时跳过本轮（断线语义，与 MQTT 引擎一致）
        if (http.State != HttpConnectionState.Connected)
            return;

        var dequeued = await _buffer.DequeueAsync(MaxDrainPerRound, IForwardBuffer.HttpChannel, stoppingToken);
        if (dequeued.IsFailure)
        {
            _logger.LogError("HTTP 转发出队失败: {Error}", dequeued.Error!.Message);
            return;
        }
        if (dequeued.Value!.Count == 0)
            return;

        var committed = new List<Guid>();
        foreach (var batch in dequeued.Value)
        {
            try
            {
                var result = await http.UploadAsync(_path, batch, stoppingToken);
                if (result.IsSuccess)
                {
                    committed.Add(batch.Id);
                    NitroMetrics.HttpForwardTotal.WithLabels("success").Inc();
                }
                else
                {
                    _logger.LogWarning("HTTP 转发失败 {BatchId}: {Error}", batch.Id, result.Error?.Message);
                    await MarkFailedOrLogErrorAsync(batch.Id, result.Error?.Message ?? "unknown", stoppingToken);
                    NitroMetrics.HttpForwardTotal.WithLabels("failure").Inc();
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "HTTP 转发异常 {BatchId}", batch.Id);
                await MarkFailedOrLogErrorAsync(batch.Id, ex.Message, stoppingToken);
                NitroMetrics.HttpForwardTotal.WithLabels("failure").Inc();
            }
        }

        if (committed.Count > 0)
        {
            // 批次已上传成功，提交不因停机取消而跳过（与 DrainOnShutdownAsync 语义一致），
            // 避免 BackgroundService.StopAsync 取消令牌与提交之间的竞态导致已成功批次滞留 InFlight。
            var commitResult = await _buffer.CommitAsync(committed, CancellationToken.None);
            if (commitResult.IsFailure)
            {
                _logger.LogError("HTTP 转发批次提交失败 {Count} 批: {Error}", committed.Count, commitResult.Error!.Message);
            }
        }
    }

    /// <summary>停机排空：MQTT 引擎同类语义——HTTP 仍可达期间把剩余 http 批次尽量发完再退出。</summary>
    private async Task DrainOnShutdownAsync()
    {
        var drainDeadline = DateTime.UtcNow + DrainTimeout;
        while (DateTime.UtcNow < drainDeadline)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var http = scope.ServiceProvider.GetRequiredService<IHttpClient>();
                if (http.State != HttpConnectionState.Connected)
                    break;

                var dequeued = await _buffer.DequeueAsync(MaxDrainPerRound, IForwardBuffer.HttpChannel, CancellationToken.None);
                if (dequeued.IsFailure || dequeued.Value!.Count == 0)
                    break;

                var committed = new List<Guid>();
                foreach (var batch in dequeued.Value)
                {
                    var result = await http.UploadAsync(_path, batch, CancellationToken.None);
                    if (result.IsSuccess)
                        committed.Add(batch.Id);
                    else
                        await MarkFailedOrLogErrorAsync(batch.Id, result.Error?.Message ?? "unknown", CancellationToken.None);
                }
                if (committed.Count > 0)
                {
                    var commitResult = await _buffer.CommitAsync(committed, CancellationToken.None);
                    if (commitResult.IsFailure)
                        _logger.LogError("HTTP 停机排空提交失败: {Error}", commitResult.Error!.Message);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "HTTP 停机排空异常，结束排空");
                break;
            }
        }
    }

    /// <summary>标记失败并检查结果：MarkFailed 失败会让批次卡在 InFlight（仅重启时恢复），记 Error 告警。</summary>
    private async Task MarkFailedOrLogErrorAsync(Guid batchId, string reason, CancellationToken ct)
    {
        var markResult = await _buffer.MarkFailedAsync(batchId, reason, ct);
        if (markResult.IsFailure)
        {
            _logger.LogError("标记批次 {BatchId} 失败（批次将卡 InFlight）: {Error}", batchId, markResult.Error!.Message);
        }
    }
}
