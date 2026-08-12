using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NitroGateway.Alarm.Repository;
using NitroGateway.Persistence.Sqlite;
using NitroGateway.Shared;
using Xunit;
using AlarmDomain = NitroGateway.Alarm.Domain;

namespace NitroGateway.UnitTests;

/// <summary>
/// SqliteAlarmRepository / SqliteAlarmRuleRepository（EF Core 版）测试（ADR-002）：
/// 告警保存/状态更新/活跃与历史查询、规则 upsert/按设备批量查询/删除，
/// 以及异常统一分类（表缺失时返回 OperationResult 而非抛异常）。
/// </summary>
public class SqliteAlarmRepositoryTests
{
    /// <summary>临时文件库：按 M005 迁移结构建 alarms / alarm_rules 表，释放时删除文件。</summary>
    private sealed class TempAlarmDb : IDisposable
    {
        public string ConnectionString { get; }

        private readonly string _path;

        public TempAlarmDb()
        {
            _path = Path.Combine(Path.GetTempPath(), $"ntg-alarm-{Guid.NewGuid():N}.db");
            ConnectionString = $"Data Source={_path};Pooling=False";
            using var conn = new SqliteConnection(ConnectionString);
            conn.Open();
            using var command = conn.CreateCommand();
            command.CommandText = """
                CREATE TABLE alarm_rules (
                    id TEXT PRIMARY KEY,
                    device_id TEXT NOT NULL,
                    point_id TEXT NOT NULL,
                    operator TEXT NOT NULL,
                    threshold REAL NOT NULL,
                    threshold_upper REAL NULL,
                    duration_seconds INTEGER NOT NULL DEFAULT 0,
                    severity TEXT NOT NULL,
                    message_template TEXT NULL,
                    enabled INTEGER NOT NULL DEFAULT 1
                );
                CREATE TABLE alarms (
                    id TEXT PRIMARY KEY,
                    rule_id TEXT NOT NULL,
                    device_id TEXT NOT NULL,
                    point_id TEXT NOT NULL,
                    trigger_value REAL NULL,
                    threshold REAL NULL,
                    severity TEXT NOT NULL,
                    message TEXT NOT NULL DEFAULT '',
                    state TEXT NOT NULL,
                    first_exceeded_at TEXT NULL,
                    occurred_at TEXT NOT NULL,
                    acknowledged_at TEXT NULL,
                    resolved_at TEXT NULL,
                    site_id TEXT NOT NULL DEFAULT ''
                );
                """;
            command.ExecuteNonQuery();
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(_path)) File.Delete(_path);
        }
    }

    private readonly TempAlarmDb _db = new();

    private NitroGatewayDbContext CreateContext()
        => new(new DbContextOptionsBuilder<NitroGatewayDbContext>().UseSqlite(_db.ConnectionString).Options);

    private static AlarmDomain.Alarm NewAlarm(Guid deviceId) => new()
    {
        Id = Guid.NewGuid(),
        RuleId = Guid.NewGuid(),
        DeviceId = deviceId,
        PointId = Guid.NewGuid(),
        TriggerValue = 80.5,
        Threshold = 70,
        Severity = AlarmDomain.AlarmSeverity.Warning,
        Message = "温度超限",
        State = AlarmDomain.AlarmState.Active,
        FirstExceededAt = DateTime.UtcNow.AddSeconds(-10),
        OccurredAt = DateTime.UtcNow
    };

    private static AlarmDomain.AlarmRule NewRule(Guid deviceId, Guid pointId, bool enabled = true) => new()
    {
        Id = Guid.NewGuid(),
        DeviceId = deviceId,
        PointId = pointId,
        Operator = ">",
        Threshold = 70,
        Severity = AlarmDomain.AlarmSeverity.Warning,
        DurationSeconds = 0,
        Enabled = enabled
    };

    [Fact]
    public async Task SaveAsync_NewAlarm_GetActiveByDeviceReturnsIt()
    {
        var repo = new SqliteAlarmRepository(CreateContext(), NullLogger<SqliteAlarmRepository>.Instance);
        var alarm = NewAlarm(Guid.NewGuid());

        var save = await repo.SaveAsync(alarm);
        Assert.True(save.IsSuccess);

        var result = await repo.GetActiveByDeviceAsync(alarm.DeviceId);
        Assert.True(result.IsSuccess);
        var item = Assert.Single(result.Value!);
        Assert.Equal(alarm.Id, item.Id);
        Assert.Equal(AlarmDomain.AlarmSeverity.Warning, item.Severity);
    }

    [Fact]
    public async Task SaveAsync_NewAlarm_GetAllActiveReturnsIt()
    {
        var repo = new SqliteAlarmRepository(CreateContext(), NullLogger<SqliteAlarmRepository>.Instance);
        var alarm = NewAlarm(Guid.NewGuid());

        await repo.SaveAsync(alarm);

        var result = await repo.GetAllActiveAsync();
        Assert.Single(result.Value!);
    }

    [Fact]
    public async Task UpdateStateAsync_Resolved_SetsResolvedAtAndExcludesFromActive()
    {
        var repo = new SqliteAlarmRepository(CreateContext(), NullLogger<SqliteAlarmRepository>.Instance);
        var alarm = NewAlarm(Guid.NewGuid());
        await repo.SaveAsync(alarm);

        await repo.UpdateStateAsync(alarm.Id, AlarmDomain.AlarmState.Resolved);

        var active = await repo.GetAllActiveAsync();
        Assert.Empty(active.Value!);
        var history = await repo.QueryAsync(DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(1));
        var resolved = Assert.Single(history.Value!);
        Assert.NotNull(resolved.ResolvedAt);
    }

    [Fact]
    public async Task QueryAsync_TimeRange_Filters()
    {
        var repo = new SqliteAlarmRepository(CreateContext(), NullLogger<SqliteAlarmRepository>.Instance);
        var alarm = NewAlarm(Guid.NewGuid());
        await repo.SaveAsync(alarm);

        var inside = await repo.QueryAsync(DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(1));
        Assert.Single(inside.Value!);

        var outside = await repo.QueryAsync(DateTime.UtcNow.AddDays(-2), DateTime.UtcNow.AddDays(-1));
        Assert.Empty(outside.Value!);
    }

    [Fact]
    public async Task QueryAsync_Limit_TruncatesResults()
    {
        // ADR-022 P2-2：limit 夹紧并 Take，防大窗口历史告警全量进内存
        var repo = new SqliteAlarmRepository(CreateContext(), NullLogger<SqliteAlarmRepository>.Instance);
        for (var i = 0; i < 3; i++)
            await repo.SaveAsync(NewAlarm(Guid.NewGuid()));

        var limited = await repo.QueryAsync(DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(1), limit: 2);

        Assert.Equal(2, limited.Value!.Count);
    }

    [Fact]
    public async Task RuleSaveAsync_NewRule_GetByPointReturnsIt()
    {
        var repo = new SqliteAlarmRuleRepository(CreateContext(), NullLogger<SqliteAlarmRuleRepository>.Instance);
        var deviceId = Guid.NewGuid();
        var pointId = Guid.NewGuid();
        var rule = NewRule(deviceId, pointId);

        var save = await repo.SaveAsync(rule);
        Assert.True(save.IsSuccess);

        var result = await repo.GetByPointAsync(deviceId, pointId);
        var item = Assert.Single(result.Value!);
        Assert.Equal(rule.Id, item.Id);
    }

    [Fact]
    public async Task RuleSaveAsync_SameId_UpdatesExisting()
    {
        var repo = new SqliteAlarmRuleRepository(CreateContext(), NullLogger<SqliteAlarmRuleRepository>.Instance);
        var deviceId = Guid.NewGuid();
        var pointId = Guid.NewGuid();
        var rule = NewRule(deviceId, pointId);
        await repo.SaveAsync(rule);

        var updated = new AlarmDomain.AlarmRule
        {
            Id = rule.Id,
            DeviceId = deviceId,
            PointId = pointId,
            Operator = ">=",
            Threshold = 90,
            Severity = AlarmDomain.AlarmSeverity.Critical,
            DurationSeconds = 5,
            Enabled = true
        };
        await repo.SaveAsync(updated);

        var result = await repo.GetByPointAsync(deviceId, pointId);
        var item = Assert.Single(result.Value!);
        Assert.Equal(90, item.Threshold);
        Assert.Equal(AlarmDomain.AlarmSeverity.Critical, item.Severity);
    }

    [Fact]
    public async Task GetByDeviceAsync_ReturnsOnlyEnabledRules()
    {
        var repo = new SqliteAlarmRuleRepository(CreateContext(), NullLogger<SqliteAlarmRuleRepository>.Instance);
        var deviceId = Guid.NewGuid();
        var enabled = NewRule(deviceId, Guid.NewGuid());
        var disabled = NewRule(deviceId, Guid.NewGuid(), enabled: false);
        await repo.SaveAsync(enabled);
        await repo.SaveAsync(disabled);

        var result = await repo.GetByDeviceAsync(deviceId);

        var item = Assert.Single(result.Value!);
        Assert.Equal(enabled.Id, item.Id);
    }

    [Fact]
    public async Task DeleteAsync_RemovesRule()
    {
        var repo = new SqliteAlarmRuleRepository(CreateContext(), NullLogger<SqliteAlarmRuleRepository>.Instance);
        var rule = NewRule(Guid.NewGuid(), Guid.NewGuid());
        await repo.SaveAsync(rule);

        var del = await repo.DeleteAsync(rule.Id);
        Assert.True(del.IsSuccess);

        var result = await repo.GetAllAsync();
        Assert.Empty(result.Value!);
    }

    /// <summary>P1-1：仓储异常必须返回 OperationResult（不向调用方抛 SQLite 异常）</summary>
    [Fact]
    public async Task SaveAsync_TableMissing_ReturnsFailureNotException()
    {
        // 只建 alarms 表、不建 alarm_rules 表
        using var conn = new SqliteConnection(_db.ConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DROP TABLE alarm_rules";
        cmd.ExecuteNonQuery();

        var repo = new SqliteAlarmRuleRepository(CreateContext(), NullLogger<SqliteAlarmRuleRepository>.Instance);

        var result = await repo.SaveAsync(NewRule(Guid.NewGuid(), Guid.NewGuid()));

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCategory.Storage, result.Error!.Category);
    }
}
