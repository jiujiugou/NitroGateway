using Microsoft.Data.Sqlite;
using NitroGateway.Alarm.Repository;
using NitroGateway.Shared;
using AlarmDomain = NitroGateway.Alarm.Domain;

namespace NitroGateway.Persistence.Sqlite;

/// <summary>SQLite 告警记录持久化</summary>
public sealed class SqliteAlarmRepository : IAlarmRepository
{
    private readonly SqliteConnection _connection;

    public SqliteAlarmRepository(SqliteConnection connection) { _connection = connection; }

    public async Task<OperationResult> SaveAsync(AlarmDomain.Alarm alarm, CancellationToken ct = default)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"INSERT INTO alarms (id,rule_id,device_id,point_id,trigger_value,threshold,severity,message,state,first_exceeded_at,occurred_at) VALUES (@id,@rid,@did,@pid,@tv,@th,@sev,@msg,@st,@fea,@oa)";
        cmd.Parameters.AddWithValue("@id", alarm.Id.ToString());
        cmd.Parameters.AddWithValue("@rid", alarm.RuleId.ToString());
        cmd.Parameters.AddWithValue("@did", alarm.DeviceId.ToString());
        cmd.Parameters.AddWithValue("@pid", alarm.PointId.ToString());
        cmd.Parameters.AddWithValue("@tv", alarm.TriggerValue);
        cmd.Parameters.AddWithValue("@th", alarm.Threshold);
        cmd.Parameters.AddWithValue("@sev", alarm.Severity.ToString());
        cmd.Parameters.AddWithValue("@msg", alarm.Message);
        cmd.Parameters.AddWithValue("@st", alarm.State.ToString());
        cmd.Parameters.AddWithValue("@fea", alarm.FirstExceededAt.ToString("O"));
        cmd.Parameters.AddWithValue("@oa", alarm.OccurredAt.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
        return OperationResult.Success();
    }

    public async Task<OperationResult> UpdateStateAsync(Guid alarmId, AlarmDomain.AlarmState state, CancellationToken ct = default)
    {
        using var cmd = _connection.CreateCommand();
        if (state == AlarmDomain.AlarmState.Resolved)
        {
            cmd.CommandText = "UPDATE alarms SET state=@st, resolved_at=@ra WHERE id=@id";
            cmd.Parameters.AddWithValue("@ra", DateTime.UtcNow.ToString("O"));
        }
        else if (state == AlarmDomain.AlarmState.Acknowledged)
        {
            cmd.CommandText = "UPDATE alarms SET state=@st, acknowledged_at=@aa WHERE id=@id";
            cmd.Parameters.AddWithValue("@aa", DateTime.UtcNow.ToString("O"));
        }
        else cmd.CommandText = "UPDATE alarms SET state=@st WHERE id=@id";
        cmd.Parameters.AddWithValue("@st", state.ToString());
        cmd.Parameters.AddWithValue("@id", alarmId.ToString());
        await cmd.ExecuteNonQueryAsync(ct);
        return OperationResult.Success();
    }

    public async Task<OperationResult<IReadOnlyList<AlarmDomain.Alarm>>> GetActiveByDeviceAsync(Guid deviceId, CancellationToken ct = default)
    {
        var alarms = new List<AlarmDomain.Alarm>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT id,rule_id,device_id,point_id,trigger_value,threshold,severity,message,state,first_exceeded_at,occurred_at,acknowledged_at,resolved_at FROM alarms WHERE device_id=@did AND state IN ('Active','Acknowledged') ORDER BY occurred_at DESC";
        cmd.Parameters.AddWithValue("@did", deviceId.ToString());
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) alarms.Add(Map(r));
        return alarms;
    }

    public async Task<OperationResult<IReadOnlyList<AlarmDomain.Alarm>>> GetAllActiveAsync(CancellationToken ct = default)
    {
        var alarms = new List<AlarmDomain.Alarm>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT id,rule_id,device_id,point_id,trigger_value,threshold,severity,message,state,first_exceeded_at,occurred_at,acknowledged_at,resolved_at FROM alarms WHERE state IN ('Active','Acknowledged') ORDER BY occurred_at DESC";
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) alarms.Add(Map(r));
        return alarms;
    }

    public async Task<OperationResult<IReadOnlyList<AlarmDomain.Alarm>>> QueryAsync(DateTime from, DateTime to, CancellationToken ct = default)
    {
        var alarms = new List<AlarmDomain.Alarm>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT id,rule_id,device_id,point_id,trigger_value,threshold,severity,message,state,first_exceeded_at,occurred_at,acknowledged_at,resolved_at FROM alarms WHERE occurred_at BETWEEN @from AND @to ORDER BY occurred_at DESC";
        cmd.Parameters.AddWithValue("@from", from.ToString("O"));
        cmd.Parameters.AddWithValue("@to", to.ToString("O"));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) alarms.Add(Map(r));
        return alarms;
    }

    private static AlarmDomain.Alarm Map(SqliteDataReader r) => new()
    {
        Id = Guid.Parse(r.GetString(0)), RuleId = Guid.Parse(r.GetString(1)), DeviceId = Guid.Parse(r.GetString(2)),
        PointId = Guid.Parse(r.GetString(3)), TriggerValue = r.IsDBNull(4) ? 0 : r.GetDouble(4),
        Threshold = r.IsDBNull(5) ? 0 : r.GetDouble(5), Severity = Enum.Parse<AlarmDomain.AlarmSeverity>(r.GetString(6)),
        Message = r.GetString(7), State = Enum.Parse<AlarmDomain.AlarmState>(r.GetString(8)),
        FirstExceededAt = r.IsDBNull(9) ? DateTime.MinValue : DateTime.Parse(r.GetString(9)),
        OccurredAt = DateTime.Parse(r.GetString(10)), AcknowledgedAt = r.IsDBNull(11) ? null : DateTime.Parse(r.GetString(11)),
        ResolvedAt = r.IsDBNull(12) ? null : DateTime.Parse(r.GetString(12))
    };
}
