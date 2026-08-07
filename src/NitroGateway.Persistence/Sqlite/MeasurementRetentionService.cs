using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NitroGateway.Storage.TimeSeries;

namespace NitroGateway.Persistence.Sqlite;

/// <summary>
/// 时序数据保留后台任务（ADR-002 P1-2）。
/// 周期性调用 <see cref="IMeasurementStore.PurgeAsync"/> 删除超过保留期的 measurements，
/// 防止时序表无限增长。保留天数与执行间隔由 DI 注册时从配置注入，默认 30 天 / 24 小时。
/// 单次清理失败只记日志，不中断服务，下个周期自动重试。
/// </summary>
public sealed class MeasurementRetentionService : BackgroundService
{
    private readonly IMeasurementStore _store;
    private readonly ILogger<MeasurementRetentionService> _logger;
    private readonly int _retentionDays;
    private readonly TimeSpan _interval;

    /// <param name="retentionDays">保留天数，最小 1 天</param>
    /// <param name="interval">清理执行间隔，最小 1 秒（测试可注入小间隔）</param>
    public MeasurementRetentionService(
        IMeasurementStore store,
        ILogger<MeasurementRetentionService> logger,
        int retentionDays = 30,
        TimeSpan? interval = null)
    {
        _store = store;
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
            var result = await _store.PurgeAsync(before, ct);
            if (result.IsSuccess)
            {
                _logger.LogInformation("时序数据保留清理完成：删除 {Before:O} 之前的数据", before);
            }
            else
            {
                // ADR-002 P1-2：清理失败不中断后台服务，等待下个周期重试
                _logger.LogError("时序数据保留清理失败: {Error}", result.Error!.Message);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 正常停机
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "时序数据保留清理异常");
        }
    }
}
