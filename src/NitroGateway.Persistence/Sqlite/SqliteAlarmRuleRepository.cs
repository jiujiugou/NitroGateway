using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NitroGateway.Alarm.Repository;
using NitroGateway.Shared;
using AlarmDomain = NitroGateway.Alarm.Domain;

namespace NitroGateway.Persistence.Sqlite;

/// <summary>
/// SQLite 告警规则持久化（EF Core）。
/// ADR-002 P1-1：统一异常分类；P2-3：新增 GetByDeviceAsync 支持按设备批量加载规则。
/// </summary>
public sealed class SqliteAlarmRuleRepository : IAlarmRuleRepository
{
    private readonly NitroGatewayDbContext _db;
    private readonly ILogger<SqliteAlarmRuleRepository> _logger;

    /// <summary>注入 EF 上下文与日志；依赖 DI 保证上下文生命周期不超出仓储</summary>
    public SqliteAlarmRuleRepository(NitroGatewayDbContext db, ILogger<SqliteAlarmRuleRepository> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<OperationResult<IReadOnlyList<AlarmDomain.AlarmRule>>> GetByPointAsync(
        Guid deviceId, Guid pointId, CancellationToken ct = default)
    {
        try
        {
            var rows = await _db.AlarmRules
                .AsNoTracking()
                .Where(r => r.DeviceId == deviceId.ToString() &&
                            r.PointId == pointId.ToString() &&
                            r.Enabled)
                .ToListAsync(ct);
            return rows.Select(ToDomain).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError("告警规则查询失败: {Error}", ex.Message);
            return Classify(ex, "告警规则查询失败");
        }
    }

    /// <inheritdoc />
    public async Task<OperationResult<IReadOnlyList<AlarmDomain.AlarmRule>>> GetByDeviceAsync(
        Guid deviceId, CancellationToken ct = default)
    {
        try
        {
            var rows = await _db.AlarmRules
                .AsNoTracking()
                .Where(r => r.DeviceId == deviceId.ToString() && r.Enabled)
                .ToListAsync(ct);
            return rows.Select(ToDomain).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError("告警规则查询失败: {Error}", ex.Message);
            return Classify(ex, "告警规则查询失败");
        }
    }

    /// <inheritdoc />
    public async Task<OperationResult<IReadOnlyList<AlarmDomain.AlarmRule>>> GetAllAsync(CancellationToken ct = default)
    {
        try
        {
            var rows = await _db.AlarmRules
                .AsNoTracking()
                .Where(r => r.Enabled)
                .ToListAsync(ct);
            return rows.Select(ToDomain).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError("告警规则查询失败: {Error}", ex.Message);
            return Classify(ex, "告警规则查询失败");
        }
    }

    /// <inheritdoc />
    public async Task<OperationResult<IReadOnlyList<AlarmDomain.AlarmRule>>> GetAllIncludingDisabledAsync(
        CancellationToken ct = default)
    {
        try
        {
            // ADR-043：管理页需要展示/恢复禁用规则，故不受 Enabled 过滤，全量返回。
            var rows = await _db.AlarmRules
                .AsNoTracking()
                .ToListAsync(ct);
            return rows.Select(ToDomain).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError("告警规则查询失败: {Error}", ex.Message);
            return Classify(ex, "告警规则查询失败");
        }
    }

    /// <inheritdoc />
    public async Task<OperationResult> SaveAsync(AlarmDomain.AlarmRule rule, CancellationToken ct = default)
    {
        try
        {
            var entity = await _db.AlarmRules.FindAsync([rule.Id.ToString()], ct);
            if (entity is null)
            {
                _db.AlarmRules.Add(ToEntity(rule));
            }
            else
            {
                _db.Entry(entity).CurrentValues.SetValues(ToEntity(rule));
            }
            await _db.SaveChangesAsync(ct);
            return OperationResult.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError("告警规则保存失败: {Error}", ex.Message);
            return Classify(ex, "告警规则保存失败");
        }
    }

    /// <inheritdoc />
    public async Task<OperationResult> DeleteAsync(Guid ruleId, CancellationToken ct = default)
    {
        try
        {
            var entity = await _db.AlarmRules.FindAsync([ruleId.ToString()], ct);
            if (entity is not null)
            {
                _db.AlarmRules.Remove(entity);
                await _db.SaveChangesAsync(ct);
            }
            return OperationResult.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError("告警规则删除失败: {Error}", ex.Message);
            return Classify(ex, "告警规则删除失败");
        }
    }

    /// <summary>
    /// EF 实体 → 领域模型。Operator 保持字符串原样（由告警评估层解释），
    /// Severity 按枚举字符串解析。
    /// </summary>
    private static AlarmDomain.AlarmRule ToDomain(AlarmRuleEntity e) => new()
    {
        Id = Guid.Parse(e.Id),
        DeviceId = Guid.Parse(e.DeviceId),
        PointId = Guid.Parse(e.PointId),
        Operator = e.Operator,
        Threshold = e.Threshold,
        ThresholdUpper = e.ThresholdUpper,
        DurationSeconds = e.DurationSeconds,
        Severity = Enum.Parse<AlarmDomain.AlarmSeverity>(e.Severity),
        MessageTemplate = e.MessageTemplate,
        Enabled = e.Enabled
    };

    /// <summary>领域模型 → EF 实体；枚举（Severity）转字符串存储</summary>
    private static AlarmRuleEntity ToEntity(AlarmDomain.AlarmRule r) => new()
    {
        Id = r.Id.ToString(),
        DeviceId = r.DeviceId.ToString(),
        PointId = r.PointId.ToString(),
        Operator = r.Operator,
        Threshold = r.Threshold,
        ThresholdUpper = r.ThresholdUpper,
        DurationSeconds = r.DurationSeconds,
        Severity = r.Severity.ToString(),
        MessageTemplate = r.MessageTemplate,
        Enabled = r.Enabled
    };

    /// <summary>
    /// 统一异常归类：EF 的 DbUpdateException 内层才是真正的 SqliteException，
    /// 解包后交给 <see cref="SqliteErrorClassifier"/> 映射为对应的 OperationalError。
    /// </summary>
    private static OperationalError Classify(Exception ex, string context)
    {
        var inner = (ex as DbUpdateException)?.InnerException;
        return SqliteErrorClassifier.Classify(inner ?? ex, context);
    }
}
