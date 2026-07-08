using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NitroGateway.Domain.Devices;
using NitroGateway.Shared;
using NitroGateway.Storage.TimeSeries;
using NitroGateway.Telemetry.Tracing;

namespace NitroGateway.Persistence.Sqlite;

/// <summary>SQLite 时序数据存储实现。手工 SQL + FluentMigrator 管理 Schema</summary>
public sealed class SqliteMeasurementStore : IMeasurementStore, IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SqliteCommand _insertCmd;
    private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public SqliteMeasurementStore(SqliteConnection connection)
    {
        _connection = connection;
        _connection.Open();

        _insertCmd = _connection.CreateCommand();
        _insertCmd.CommandText = @"
            INSERT INTO measurements (id, device_id, point_id, point_name, raw_value, value, data_type, timestamp, quality, error_msg)
            VALUES (@id, @did, @pid, @name, @raw, @val, @type, @ts, @qual, @err)";
        _insertCmd.Parameters.Add(new SqliteParameter("@id", SqliteType.Text));
        _insertCmd.Parameters.Add(new SqliteParameter("@did", SqliteType.Text));
        _insertCmd.Parameters.Add(new SqliteParameter("@pid", SqliteType.Text));
        _insertCmd.Parameters.Add(new SqliteParameter("@name", SqliteType.Text));
        _insertCmd.Parameters.Add(new SqliteParameter("@raw", SqliteType.Text));
        _insertCmd.Parameters.Add(new SqliteParameter("@val", SqliteType.Real));
        _insertCmd.Parameters.Add(new SqliteParameter("@type", SqliteType.Text));
        _insertCmd.Parameters.Add(new SqliteParameter("@ts", SqliteType.Text));
        _insertCmd.Parameters.Add(new SqliteParameter("@qual", SqliteType.Text));
        _insertCmd.Parameters.Add(new SqliteParameter("@err", SqliteType.Text));
    }

    public async Task<OperationResult> WriteAsync(
        IReadOnlyList<PointSnapshot> snapshots, CancellationToken ct = default)
    {
        using var activity = GatewayActivitySource.Source.StartActivity(GatewayActivities.SqliteWrite);
        activity?.SetTag(GatewayActivityTags.TableName, "measurements");
        activity?.SetTag(GatewayActivityTags.SnapshotCount, snapshots.Count);

        if (snapshots.Count == 0)
        {
            activity?.SetStatus(ActivityStatusCode.Ok);
            return OperationResult.Success();
        }

        await using var tx = await _connection.BeginTransactionAsync(ct);
        _insertCmd.Transaction = (SqliteTransaction)tx;

        try
        {
            foreach (var s in snapshots)
            {
                _insertCmd.Parameters["@id"].Value = Guid.NewGuid().ToString();
                _insertCmd.Parameters["@did"].Value = s.DeviceId.ToString();
                _insertCmd.Parameters["@pid"].Value = s.DevicePointId.ToString();
                _insertCmd.Parameters["@name"].Value = "";
                _insertCmd.Parameters["@raw"].Value = SerializeRawValue(s.RawValue);
                _insertCmd.Parameters["@val"].Value = s.Value is IConvertible ? Convert.ToDouble(s.Value) : DBNull.Value;
                _insertCmd.Parameters["@type"].Value = "";
                _insertCmd.Parameters["@ts"].Value = s.Timestamp.ToString("O");
                _insertCmd.Parameters["@qual"].Value = s.Quality.ToString();
                _insertCmd.Parameters["@err"].Value = (object?)s.ErrorMessage ?? DBNull.Value;

                await _insertCmd.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
            activity?.SetStatus(ActivityStatusCode.Ok);
            return OperationResult.Success();
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(ct);
            activity?.SetStatus(ActivityStatusCode.Error);
            activity?.SetTag(GatewayActivityTags.ErrorMessage, ex.ToString());
            return SqliteErrorClassifier.Classify(ex, "时序数据写入失败");
        }
    }

    public async Task<OperationResult<IReadOnlyList<PointSnapshot>>> QueryAsync(
        Guid deviceId, Guid pointId, DateTime from, DateTime to, CancellationToken ct = default)
    {
        var results = new List<PointSnapshot>();

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            SELECT device_id, point_id, raw_value, value, timestamp, quality, error_msg
            FROM measurements
            WHERE device_id = @did AND point_id = @pid
              AND timestamp BETWEEN @from AND @to
            ORDER BY timestamp ASC";
        cmd.Parameters.AddWithValue("@did", deviceId.ToString());
        cmd.Parameters.AddWithValue("@pid", pointId.ToString());
        cmd.Parameters.AddWithValue("@from", from.ToString("O"));
        cmd.Parameters.AddWithValue("@to", to.ToString("O"));

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new PointSnapshot
            {
                DeviceId = Guid.Parse(reader.GetString(0)),
                DevicePointId = Guid.Parse(reader.GetString(1)),
                RawValue = DeserializeRawValue(reader.IsDBNull(2) ? null : reader.GetString(2)),
                Value = reader.IsDBNull(3) ? null : reader.GetDouble(3),
                Timestamp = DateTime.Parse(reader.GetString(4)),
                Quality = Enum.Parse<QualityCode>(reader.GetString(5)),
                ErrorMessage = reader.IsDBNull(6) ? null : reader.GetString(6)
            });
        }

        return results;
    }

    public async Task<OperationResult> PurgeAsync(DateTime before, CancellationToken ct = default)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM measurements WHERE timestamp < @before";
        cmd.Parameters.AddWithValue("@before", before.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
        return OperationResult.Success();
    }

    public void Dispose() => _connection.Dispose();

    private string? SerializeRawValue(object? raw)
    {
        if (raw is null) return null;
        if (raw is ushort[] regs) return JsonSerializer.Serialize(regs, _jsonOptions);
        return JsonSerializer.Serialize(raw, _jsonOptions);
    }

    private object? DeserializeRawValue(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try { return JsonSerializer.Deserialize<ushort[]>(json, _jsonOptions); }
        catch { return json; }
    }
}
