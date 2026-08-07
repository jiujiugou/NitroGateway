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

    /// <summary>EF 实体 → 领域模型</summary>
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

    /// <summary>领域模型 → EF 实体</summary>
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

    /// <summary>解包 DbUpdateException 后统一走 SqliteErrorClassifier</summary>
    private static OperationalError Classify(Exception ex, string context)
    {
        var inner = (ex as DbUpdateException)?.InnerException;
        return SqliteErrorClassifier.Classify(inner ?? ex, context);
    }
}
