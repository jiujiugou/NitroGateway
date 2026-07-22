using Microsoft.Data.Sqlite;
using NitroGateway.Alarm.Repository;
using NitroGateway.Shared;
using AlarmDomain = NitroGateway.Alarm.Domain;

namespace NitroGateway.Persistence.Sqlite;

/// <summary>SQLite 告警规则持久化</summary>
public sealed class SqliteAlarmRuleRepository : IAlarmRuleRepository
{
    private readonly SqliteConnection _connection;

    public SqliteAlarmRuleRepository(SqliteConnection connection) { _connection = connection; }

    public async Task<OperationResult<IReadOnlyList<AlarmDomain.AlarmRule>>> GetByPointAsync(
        Guid deviceId, Guid pointId, CancellationToken ct = default)
    {
        var rules = new List<AlarmDomain.AlarmRule>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT id, device_id, point_id, operator, threshold, threshold_upper, duration_seconds, severity, message_template, enabled FROM alarm_rules WHERE device_id=@did AND point_id=@pid AND enabled=1";
        cmd.Parameters.AddWithValue("@did", deviceId.ToString());
        cmd.Parameters.AddWithValue("@pid", pointId.ToString());
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) rules.Add(Map(reader));
        return rules;
    }

    public async Task<OperationResult<IReadOnlyList<AlarmDomain.AlarmRule>>> GetAllAsync(CancellationToken ct = default)
    {
        var rules = new List<AlarmDomain.AlarmRule>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT id, device_id, point_id, operator, threshold, threshold_upper, duration_seconds, severity, message_template, enabled FROM alarm_rules WHERE enabled=1";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) rules.Add(Map(reader));
        return rules;
    }

    public async Task<OperationResult> SaveAsync(AlarmDomain.AlarmRule rule, CancellationToken ct = default)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"INSERT OR REPLACE INTO alarm_rules (id,device_id,point_id,operator,threshold,threshold_upper,duration_seconds,severity,message_template,enabled) VALUES (@id,@did,@pid,@op,@th,@thu,@dur,@sev,@msg,@en)";
        cmd.Parameters.AddWithValue("@id", rule.Id.ToString());
        cmd.Parameters.AddWithValue("@did", rule.DeviceId.ToString());
        cmd.Parameters.AddWithValue("@pid", rule.PointId.ToString());
        cmd.Parameters.AddWithValue("@op", rule.Operator);
        cmd.Parameters.AddWithValue("@th", rule.Threshold);
        cmd.Parameters.AddWithValue("@thu", (object?)rule.ThresholdUpper ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@dur", rule.DurationSeconds);
        cmd.Parameters.AddWithValue("@sev", rule.Severity.ToString());
        cmd.Parameters.AddWithValue("@msg", (object?)rule.MessageTemplate ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@en", rule.Enabled);
        await cmd.ExecuteNonQueryAsync(ct);
        return OperationResult.Success();
    }

    public async Task<OperationResult> DeleteAsync(Guid ruleId, CancellationToken ct = default)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM alarm_rules WHERE id=@id";
        cmd.Parameters.AddWithValue("@id", ruleId.ToString());
        await cmd.ExecuteNonQueryAsync(ct);
        return OperationResult.Success();
    }

    private static AlarmDomain.AlarmRule Map(SqliteDataReader r) => new()
    {
        Id = Guid.Parse(r.GetString(0)), DeviceId = Guid.Parse(r.GetString(1)), PointId = Guid.Parse(r.GetString(2)),
        Operator = r.GetString(3), Threshold = r.GetDouble(4), ThresholdUpper = r.IsDBNull(5) ? null : r.GetDouble(5),
        DurationSeconds = r.GetInt32(6), Severity = Enum.Parse<AlarmDomain.AlarmSeverity>(r.GetString(7)),
        MessageTemplate = r.IsDBNull(8) ? null : r.GetString(8), Enabled = r.GetBoolean(9)
    };
}
