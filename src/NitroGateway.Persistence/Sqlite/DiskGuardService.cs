using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NitroGateway.Storage.Disk;
using NitroGateway.Telemetry;

namespace NitroGateway.Persistence.Sqlite;

/// <summary>
/// 磁盘守卫（ADR-012）：7×24 无人值守下在磁盘写满前预警并降级。
/// 按周期检查 SQLite 数据文件所在目录与 logs/ 目录的剩余空间（取最小值），
/// 等级变化（Healthy → Warning → Critical，恢复带滞后防抖）通过 <see cref="IDiskStatus.Changed"/> 通知联动方。
/// <para><b>边界：</b>只评估不干预——降级动作由消费方（DataDispatcher 暂停写入、ForwarderEngine 暂停出队、
/// DiskHealthCheck 报告）执行；SQLITE_FULL 兜底分类（ADR-002 P3-4）语义不变。</para>
/// </summary>
public sealed class DiskGuardService : BackgroundService, IDiskStatus
{
    private readonly string _dbDirectory;
    private readonly DiskGuardOption _option;
    private readonly ILogger<DiskGuardService> _logger;

    /// <summary>当前等级；volatile 保证热路径（采集/转发）读到的不是过期缓存</summary>
    private volatile DiskLevel _level = DiskLevel.Healthy;

    /// <inheritdoc />
    public DiskLevel Level => _level;

    /// <inheritdoc />
    public event Action<DiskLevel>? Changed;

    /// <summary>
    /// 创建磁盘守卫。
    /// </summary>
    /// <param name="connectionString">SQLite 连接串（Persistence:ConnectionString），从中解析 Data Source 目录</param>
    /// <param name="option">阈值与周期配置（Disk 段）</param>
    /// <param name="logger">日志</param>
    public DiskGuardService(string connectionString, IOptions<DiskGuardOption> option, ILogger<DiskGuardService> logger)
    {
        _option = option.Value;
        _logger = logger;
        _dbDirectory = ResolveDbDirectory(connectionString);
    }

    /// <summary>
    /// 主循环：按配置周期检查磁盘并更新等级；单轮异常记 Error 后继续，不影响守卫存活。
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await CheckOnceAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "磁盘检查异常，下轮重试");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(_option.IntervalSeconds), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// 执行一次检查：收集数据目录与 logs 目录剩余空间（取最小值），评估等级并发布变化事件。
    /// 供测试直接调用（内部），避免依赖后台周期。
    /// </summary>
    internal async Task CheckOnceAsync(CancellationToken ct = default)
    {
        var directories = new[] { _dbDirectory, ResolveLogsDirectory() }
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (directories.Length == 0)
        {
            // 目录均不存在（如纯测试环境）：跳过本轮，保持当前等级
            return;
        }

        var minFreeBytes = long.MaxValue;
        foreach (var dir in directories)
        {
            var freeBytes = new DriveInfo(dir).AvailableFreeSpace;
            NitroMetrics.DiskFreeBytes.WithLabels(dir).Set(freeBytes);
            minFreeBytes = Math.Min(minFreeBytes, freeBytes);
        }

        var next = Evaluate(minFreeBytes, _option, _level);
        if (next == _level)
            return;

        _level = next;
        if (next == DiskLevel.Healthy)
            _logger.LogInformation("磁盘空间已恢复: 剩余 {Free}MB，等级恢复正常", minFreeBytes / (1024 * 1024));
        else
            _logger.LogWarning("磁盘空间不足（{Level}）: 剩余 {Free}MB，阈值 Warning={Warning}MB / Critical={Critical}MB",
                next, minFreeBytes / (1024 * 1024), _option.WarningFreeBytes / (1024 * 1024), _option.CriticalFreeBytes / (1024 * 1024));

        Changed?.Invoke(next);
    }

    /// <summary>
    /// 等级评估（纯逻辑，供测试红绿对照）。含恢复滞后：进入阈值用原始阈值，
    /// 已在 Critical/Warning 中时需恢复到阈值 ×(1+margin) 才解除，避免临界抖动。
    /// </summary>
    /// <param name="freeBytes">当前剩余字节数</param>
    /// <param name="option">阈值配置</param>
    /// <param name="current">当前等级</param>
    internal static DiskLevel Evaluate(long freeBytes, DiskGuardOption option, DiskLevel current)
    {
        var margin = 1 + option.RecoveryMarginPercent / 100;

        var criticalThreshold = current == DiskLevel.Critical
            ? (long)(option.CriticalFreeBytes * margin)
            : option.CriticalFreeBytes;
        if (freeBytes < criticalThreshold)
            return DiskLevel.Critical;

        var warningThreshold = current == DiskLevel.Warning
            ? (long)(option.WarningFreeBytes * margin)
            : option.WarningFreeBytes;
        if (freeBytes < warningThreshold)
            return DiskLevel.Warning;

        return DiskLevel.Healthy;
    }

    /// <summary>从连接串解析 Data Source 所在目录（相对路径按当前工作目录解析）</summary>
    private static string ResolveDbDirectory(string connectionString)
    {
        const string key = "Data Source=";
        var index = connectionString.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        var dataSource = index >= 0
            ? connectionString[(index + key.Length)..].Split(';')[0].Trim()
            : "nitrogateway.db";

        var fullPath = Path.GetFullPath(dataSource);
        return Path.GetDirectoryName(fullPath) ?? Directory.GetCurrentDirectory();
    }

    /// <summary>logs 目录：当前工作目录下的 logs/（与 Serilog File sink 默认落点一致）</summary>
    private static string ResolveLogsDirectory()
        => Path.Combine(Directory.GetCurrentDirectory(), "logs");
}
