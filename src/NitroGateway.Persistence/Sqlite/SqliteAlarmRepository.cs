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

    /// <summary>注入 EF 上下文与日志；依赖 DI 保证上下文生命周期不超出仓储</summary>
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
        => await GetActiveByDeviceAsync(deviceId, null, ct);

    /// <inheritdoc />
    public async Task<OperationResult<IReadOnlyList<AlarmDomain.Alarm>>> GetActiveByDeviceAsync(
        Guid deviceId, string? siteId, CancellationToken ct = default)
    {
        try
        {
            // ADR-035 第 1 步：siteId 非空时按站点过滤（EF 表达式树不支持 is 模式，改为条件式 Where）
            var query = _db.Alarms
                .AsNoTracking()
                .Where(a => a.DeviceId == deviceId.ToString() &&
                            (a.State == nameof(AlarmDomain.AlarmState.Active) ||
                             a.State == nameof(AlarmDomain.AlarmState.Acknowledged)));
            if (!string.IsNullOrEmpty(siteId))
                query = query.Where(a => a.SiteId == siteId);
            var rows = await query.OrderByDescending(a => a.OccurredAt).ToListAsync(ct);
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
        => await GetAllActiveAsync(null, ct);

    /// <inheritdoc />
    public async Task<OperationResult<IReadOnlyList<AlarmDomain.Alarm>>> GetAllActiveAsync(
        string? siteId, CancellationToken ct = default)
    {
        try
        {
            var query = _db.Alarms
                .AsNoTracking()
                .Where(a => a.State == nameof(AlarmDomain.AlarmState.Active) ||
                            a.State == nameof(AlarmDomain.AlarmState.Acknowledged));
            if (!string.IsNullOrEmpty(siteId))
                query = query.Where(a => a.SiteId == siteId);
            var rows = await query.OrderByDescending(a => a.OccurredAt).ToListAsync(ct);
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
        DateTime from, DateTime to, int limit = 1000, CancellationToken ct = default)
        => await QueryAsync(from, to, null, limit, ct);

    /// <inheritdoc />
    public async Task<OperationResult<IReadOnlyList<AlarmDomain.Alarm>>> QueryAsync(
        DateTime from, DateTime to, string? siteId, int limit = 1000, CancellationToken ct = default)
    {
        try
        {
            // ADR-022 P2-2：夹紧 1..1000 并 Take，防大窗口历史告警全量进内存
            var safeLimit = Math.Clamp(limit, 1, 1000);
            var fromStr = from.ToString("O");
            var toStr = to.ToString("O");
            // 时间以 O 格式字符串存储，字符串比较与时间顺序一致
            var query = _db.Alarms
                .AsNoTracking()
                .Where(a => string.Compare(a.OccurredAt, fromStr) >= 0 &&
                            string.Compare(a.OccurredAt, toStr) <= 0);
            if (!string.IsNullOrEmpty(siteId))
                query = query.Where(a => a.SiteId == siteId);
            var rows = await query.OrderByDescending(a => a.OccurredAt).Take(safeLimit).ToListAsync(ct);
            return rows.Select(ToDomain).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError("告警历史查询失败: {Error}", ex.Message);
            return Classify(ex, "告警历史查询失败");
        }
    }

    /// <summary>
    /// EF 实体 → 领域模型。时间列解析约定：null → DateTime.MinValue（首超时）
    /// 或 null（确认/恢复）；"O" 格式字符串按本机时区解析，写入侧统一 UTC。
    /// </summary>
    private static AlarmDomain.Alarm ToDomain(AlarmEntity e) => new()
    {
        Id = Guid.Parse(e.Id),
        RuleId = Guid.Parse(e.RuleId),
        DeviceId = Guid.Parse(e.DeviceId),
        PointId = Guid.Parse(e.PointId),
        TriggerValue = e.TriggerValue ?? 0,
        Threshold = e.Threshold ?? 0,
        // ADR-018 P3-4：未知枚举字符串回退默认值，脏/历史数据不致告警读取整体失败
        Severity = ParseEnum<AlarmDomain.AlarmSeverity>(e.Severity),
        Message = e.Message,
        State = ParseEnum<AlarmDomain.AlarmState>(e.State),
        FirstExceededAt = e.FirstExceededAt is null ? DateTime.MinValue : DateTime.Parse(e.FirstExceededAt),
        OccurredAt = DateTime.Parse(e.OccurredAt),
        AcknowledgedAt = e.AcknowledgedAt is null ? null : DateTime.Parse(e.AcknowledgedAt),
        ResolvedAt = e.ResolvedAt is null ? null : DateTime.Parse(e.ResolvedAt)
    };

    /// <summary>枚举容错解析（ADR-018 P3-4）：未知字符串回退默认值，与 DomainMapper 语义一致</summary>
    private static T ParseEnum<T>(string? value) where T : struct, Enum
        => Enum.TryParse<T>(value, ignoreCase: true, out var result) ? result : default;

    /// <summary>
    /// 领域模型 → EF 实体。时间列统一转 "O" 格式字符串存储；
    /// DateTime.MinValue（领域层表示"未设置"）映射为 null，保证列语义可空。
    /// </summary>
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
