using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NitroGateway.Storage.Buffer;

namespace NitroGateway.Persistence.Sqlite;

/// <summary>
/// 死信保留后台任务（ADR-018 P2-3）。
/// 周期性调用 <see cref="IForwardBuffer.PurgeDeadLettersAsync"/> 删除超过保留期的死信，
/// 与 measurements 保留清理（<see cref="MeasurementRetentionService"/>）对称，防止坏消息无限累积。
/// 保留天数与执行间隔由 DI 注入，默认 30 天 / 24 小时；单次清理失败只记日志，下个周期自动重试。
/// </summary>
public sealed class DeadLetterRetentionService : BackgroundService
{
    private readonly IForwardBuffer _buffer;
    private readonly ILogger<DeadLetterRetentionService> _logger;
    private readonly int _retentionDays;
    private readonly TimeSpan _interval;

    /// <param name="buffer">转发缓冲（死信清理目标）</param>
    /// <param name="logger">日志记录器</param>
    /// <param name="retentionDays">死信保留天数，最小 1 天</param>
    /// <param name="interval">清理执行间隔，最小 1 秒（测试可注入小间隔）</param>
    public DeadLetterRetentionService(
        IForwardBuffer buffer,
        ILogger<DeadLetterRetentionService> logger,
        int retentionDays = 30,
        TimeSpan? interval = null)
    {
        _buffer = buffer;
        _logger = logger;
        _retentionDays = Math.Max(1, retentionDays);
        _interval = interval ?? TimeSpan.FromHours(24);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await PurgeOnceAsync(stoppingToken);

            try { await Task.Delay(_interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task PurgeOnceAsync(CancellationToken ct)
    {
        try
        {
            var before = DateTime.UtcNow.AddDays(-_retentionDays);
            var result = await _buffer.PurgeDeadLettersAsync(before, ct);
            if (result.IsSuccess)
            {
                _logger.LogInformation("死信保留清理完成：删除 {Before:O} 之前入队的死信", before);
            }
            else
            {
                _logger.LogError("死信保留清理失败: {Error}", result.Error!.Message);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 正常停机
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "死信保留清理异常");
        }
    }
}
