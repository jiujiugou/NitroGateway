using System.Diagnostics;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using NitroGateway.Domain.Devices;
using NitroGateway.Shared;
using NitroGateway.Storage.TimeSeries;
using NitroGateway.Telemetry.Tracing;

namespace NitroGateway.Persistence.Sqlite;

/// <summary>SQLite 时序数据存储实现（Dapper）</summary>
public sealed class SqliteMeasurementStore : IMeasurementStore
{
    private readonly string _connectionString;
    private readonly JsonSerializerOptions _json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public SqliteMeasurementStore(string connString) { _connectionString = connString; }

    public async Task<OperationResult> WriteAsync(IReadOnlyList<PointSnapshot> snapshots, CancellationToken ct = default)
    {
        using var activity = GatewayActivitySource.Source.StartActivity(GatewayActivities.SqliteWrite);
        activity?.SetTag(GatewayActivityTags.TableName, "measurements");
        activity?.SetTag(GatewayActivityTags.SnapshotCount, snapshots.Count);

        if (snapshots.Count == 0) { activity?.SetStatus(ActivityStatusCode.Ok); return OperationResult.Success(); }

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        try
        {
            await conn.ExecuteAsync(
                @"INSERT INTO measurements (id, device_id, point_id, point_name, raw_value, value, data_type, timestamp, quality, error_msg)
                  VALUES (@id, @did, @pid, @name, @raw, @val, @type, @ts, @qual, @err)",
                snapshots.Select(s => new
                {
                    id = Guid.NewGuid().ToString(),
                    did = s.DeviceId.ToString(),
                    pid = s.DevicePointId.ToString(),
                    name = "",
                    raw = Serialize(s.RawValue),
                    val = s.Value is IConvertible ? Convert.ToDouble(s.Value) : (object)DBNull.Value,
                    type = "",
                    ts = s.Timestamp.ToString("O"),
                    qual = s.Quality.ToString(),
                    err = (object?)s.ErrorMessage ?? DBNull.Value
                }), tx);

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
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        var rows = await conn.QueryAsync(
            @"SELECT device_id, point_id, raw_value, value, timestamp, quality, error_msg
              FROM measurements WHERE device_id = @did AND point_id = @pid AND timestamp BETWEEN @from AND @to
              ORDER BY timestamp ASC",
            new { did = deviceId.ToString(), pid = pointId.ToString(), from = from.ToString("O"), to = to.ToString("O") });

        return rows.Select(r => new PointSnapshot
        {
            DeviceId = Guid.Parse((string)r.device_id),
            DevicePointId = Guid.Parse((string)r.point_id),
            RawValue = Deserialize(r.raw_value as string),
            Value = r.value is DBNull ? null : (double)r.value,
            Timestamp = DateTime.Parse((string)r.timestamp),
            Quality = Enum.Parse<QualityCode>((string)r.quality),
            ErrorMessage = r.error_msg as string
        }).ToList();
    }

    public async Task<OperationResult> PurgeAsync(DateTime before, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await conn.ExecuteAsync("DELETE FROM measurements WHERE timestamp < @before", new { before = before.ToString("O") });
        return OperationResult.Success();
    }

    private string? Serialize(object? raw)
    {
        if (raw is null) return null;
        if (raw is ushort[] regs) return JsonSerializer.Serialize(regs, _json);
        return JsonSerializer.Serialize(raw, _json);
    }

    private object? Deserialize(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try { return JsonSerializer.Deserialize<ushort[]>(json, _json); }
        catch { return json; }
    }
}
