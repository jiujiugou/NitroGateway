using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NitroGateway.Alarm.Repository;
using NitroGateway.Shared;
using AlarmDomain = NitroGateway.Alarm.Domain;

namespace NitroGateway.Persistence.Sqlite;

/// <summary>
/// SQLite 告警记录持久化（EF Core）。
/// ADR-002 P1-1：EF 化后统一异常分类，SQLite 异常不再直接击穿 AlarmHostedService。
/// </summary>
public sealed class SqliteAlarmRepository : IAlarmRepository
{
    private readonly NitroGatewayDbContext _db;
    private readonly ILogger<SqliteAlarmRepository> _logger;

    public SqliteAlarmRepository(NitroGatewayDbContext db, ILogger<SqliteAlarmRepository> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<OperationResult> SaveAsync(AlarmDomain.Alarm alarm, CancellationToken ct = default)
    {
        try
        {
            _db.Alarms.Add(ToEntity(alarm));
            await _db.SaveChangesAsync(ct);
            return OperationResult.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError("告警保存失败: {Error}", ex.Message);
            return Classify(ex, "告警保存失败");
        }
    }

    /// <inheritdoc />
    public async Task<OperationResult> UpdateStateAsync(Guid alarmId, AlarmDomain.AlarmState state, CancellationToken ct = default)
    {
        try
        {
            var entity = await _db.Alarms.FindAsync([alarmId.ToString()], ct);
            if (entity is null) return OperationResult.Success();

            entity.State = state.ToString();
            if (state == AlarmDomain.AlarmState.Resolved)
                entity.ResolvedAt = DateTime.UtcNow.ToString("O");
            else if (state == AlarmDomain.AlarmState.Acknowledged)
                entity.AcknowledgedAt = DateTime.UtcNow.ToString("O");

            await _db.SaveChangesAsync(ct);
            return OperationResult.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError("告警状态更新失败: {Error}", ex.Message);
            return Classify(ex, "告警状态更新失败");
        }
    }

    /// <inheritdoc />
    public async Task<OperationResult<IReadOnlyList<AlarmDomain.Alarm>>> GetActiveByDeviceAsync(
        Guid deviceId, CancellationToken ct = default)
    {
        try
        {
            var rows = await _db.Alarms
                .AsNoTracking()
                .Where(a => a.DeviceId == deviceId.ToString() &&
                            (a.State == nameof(AlarmDomain.AlarmState.Active) ||
                             a.State == nameof(AlarmDomain.AlarmState.Acknowledged)))
                .OrderByDescending(a => a.OccurredAt)
                .ToListAsync(ct);
            return rows.Select(ToDomain).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError("活跃告警查询失败: {Error}", ex.Message);
            return Classify(ex, "活跃告警查询失败");
        }
    }

    /// <inheritdoc />
    public async Task<OperationResult<IReadOnlyList<AlarmDomain.Alarm>>> GetAllActiveAsync(CancellationToken ct = default)
    {
        try
        {
            var rows = await _db.Alarms
                .AsNoTracking()
                .Where(a => a.State == nameof(AlarmDomain.AlarmState.Active) ||
                            a.State == nameof(AlarmDomain.AlarmState.Acknowledged))
                .OrderByDescending(a => a.OccurredAt)
                .ToListAsync(ct);
            return rows.Select(ToDomain).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError("活跃告警查询失败: {Error}", ex.Message);
            return Classify(ex, "活跃告警查询失败");
        }
    }

    /// <inheritdoc />
    public async Task<OperationResult<IReadOnlyList<AlarmDomain.Alarm>>> QueryAsync(
        DateTime from, DateTime to, CancellationToken ct = default)
    {
        try
        {
            var fromStr = from.ToString("O");
            var toStr = to.ToString("O");
            // 时间以 O 格式字符串存储，字符串比较与时间顺序一致
            var rows = await _db.Alarms
                .AsNoTracking()
                .Where(a => string.Compare(a.OccurredAt, fromStr) >= 0 &&
                            string.Compare(a.OccurredAt, toStr) <= 0)
                .OrderByDescending(a => a.OccurredAt)
                .ToListAsync(ct);
            return rows.Select(ToDomain).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError("告警历史查询失败: {Error}", ex.Message);
            return Classify(ex, "告警历史查询失败");
        }
    }

    /// <summary>EF 实体 → 领域模型</summary>
    private static AlarmDomain.Alarm ToDomain(AlarmEntity e) => new()
    {
        Id = Guid.Parse(e.Id),
        RuleId = Guid.Parse(e.RuleId),
        DeviceId = Guid.Parse(e.DeviceId),
        PointId = Guid.Parse(e.PointId),
        TriggerValue = e.TriggerValue ?? 0,
        Threshold = e.Threshold ?? 0,
        Severity = Enum.Parse<AlarmDomain.AlarmSeverity>(e.Severity),
        Message = e.Message,
        State = Enum.Parse<AlarmDomain.AlarmState>(e.State),
        FirstExceededAt = e.FirstExceededAt is null ? DateTime.MinValue : DateTime.Parse(e.FirstExceededAt),
        OccurredAt = DateTime.Parse(e.OccurredAt),
        AcknowledgedAt = e.AcknowledgedAt is null ? null : DateTime.Parse(e.AcknowledgedAt),
        ResolvedAt = e.ResolvedAt is null ? null : DateTime.Parse(e.ResolvedAt)
    };

    /// <summary>领域模型 → EF 实体</summary>
    private static AlarmEntity ToEntity(AlarmDomain.Alarm a) => new()
    {
        Id = a.Id.ToString(),
        RuleId = a.RuleId.ToString(),
        DeviceId = a.DeviceId.ToString(),
        PointId = a.PointId.ToString(),
        TriggerValue = a.TriggerValue,
        Threshold = a.Threshold,
        Severity = a.Severity.ToString(),
        Message = a.Message,
        State = a.State.ToString(),
        FirstExceededAt = a.FirstExceededAt == DateTime.MinValue ? null : a.FirstExceededAt.ToString("O"),
        OccurredAt = a.OccurredAt.ToString("O"),
        AcknowledgedAt = a.AcknowledgedAt?.ToString("O"),
        ResolvedAt = a.ResolvedAt?.ToString("O")
    };

    /// <summary>解包 DbUpdateException 后统一走 SqliteErrorClassifier</summary>
    private static OperationalError Classify(Exception ex, string context)
    {
        var inner = (ex as DbUpdateException)?.InnerException;
        return SqliteErrorClassifier.Classify(inner ?? ex, context);
    }
}
