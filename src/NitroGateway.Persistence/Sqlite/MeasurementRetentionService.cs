using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NitroGateway.Storage.TimeSeries;

namespace NitroGateway.Persistence.Sqlite;

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
