using HslCommunication.Profinet.Siemens;
using Microsoft.Extensions.Logging;
using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Protocols;
using NitroGateway.Shared;

namespace NitroGateway.Protocols.S7;

/// <summary>Siemens S7 驱动，基于 HslCommunication。地址: DB1.DBD0, M100, I0.0</summary>
public sealed class S7Driver : IProtocolDriver, IDisposable
{
    private readonly DeviceConnection _connection;
    private readonly ILogger _logger;
    private SiemensS7Net? _client;

    public DriverState State { get; private set; } = DriverState.Disconnected;
    public DriverCapability Capability => S7DriverCapability.Instance;

    public S7Driver(DeviceConnection connection, ILogger logger)
    {
        _connection = connection;
        _logger = logger;
    }

    public Task<OperationResult> ConnectAsync(CancellationToken ct = default)
    {
        if (State == DriverState.Connected) return Task.FromResult(OperationResult.Success());
        State = DriverState.Connecting;

        try
        {
            var parts = _connection.Endpoint.Split(':');
            var ip = parts[0];
            var port = parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : 102;
            var rack = (byte)(int)(_connection.Parameters.GetValueOrDefault("Rack") ?? 0);
            var slot = (byte)(int)(_connection.Parameters.GetValueOrDefault("Slot") ?? 1);

            var cpuType = (_connection.Parameters.GetValueOrDefault("CpuType")?.ToString() ?? "S71200") switch
            {
                "S-1500" => SiemensPLCS.S1500,
                "S-300" => SiemensPLCS.S300,
                "S-400" => SiemensPLCS.S400,
                "S-1200" => SiemensPLCS.S1200
            };

            _client = new SiemensS7Net(cpuType) { IpAddress = ip, Port = port, Rack = rack, Slot = slot };
            var r = _client.ConnectServer();
            if (r.IsSuccess) { State = DriverState.Connected; return Task.FromResult(OperationResult.Success()); }

            State = DriverState.Faulted;
            return Task.FromResult<OperationResult>(OperationalError.Timeout($"S7 连接失败: {r.Message}"));
        }
        catch (Exception ex)
        {
            State = DriverState.Faulted;
            return Task.FromResult<OperationResult>(OperationalError.Timeout($"S7 连接异常: {ex.Message}"));
        }
    }

    public Task<OperationResult> DisconnectAsync(CancellationToken ct = default)
    {
        try { _client?.ConnectClose(); } catch { }
        _client = null; State = DriverState.Disconnected;
        return Task.FromResult(OperationResult.Success());
    }

    public Task<OperationResult> PingAsync(CancellationToken ct = default)
    {
        if (_client is null) return Task.FromResult<OperationResult>(OperationalError.Unavailable("S7 未连接"));
        try { var r = _client.ReadInt16("DB1.DBW0"); return Task.FromResult(r.IsSuccess ? OperationResult.Success() : (OperationResult)OperationalError.Timeout(r.Message)); }
        catch (Exception ex) { return Task.FromResult<OperationResult>(OperationalError.Timeout($"Ping 失败: {ex.Message}")); }
    }

    public Task<OperationResult<RawPointValue>> ReadAsync(DevicePoint point, CancellationToken ct = default)
    {
        if (_client is null) return Task.FromResult(OperationResult<RawPointValue>.Failure(OperationalError.Unavailable("S7 未连接")));

        try
        {
            var addr = S7AddressParser.Parse(point.Address);
            var address = addr.DbNumber > 0 ? $"DB{addr.DbNumber}.{addr.VarType}{addr.ByteOffset}" : $"{addr.VarType}{addr.ByteOffset}";

            var value = point.DataType switch
            {
                DataType.Float   => (object)_client.ReadFloat(address).Content,
                DataType.Int16   => (object)_client.ReadInt16(address).Content,
                DataType.Int32   => (object)_client.ReadInt32(address).Content,
                DataType.Bool    => (object)_client.ReadBool(address).Content,
                DataType.Double  => (object)_client.ReadDouble(address).Content,
                _ => (object)_client.ReadFloat(address).Content
            };

            var raw = new RawPointValue { Point = point, Value = value, Timestamp = DateTime.UtcNow };
            return Task.FromResult(OperationResult<RawPointValue>.Success(raw));
        }
        catch (Exception ex)
        {
            return Task.FromResult(OperationResult<RawPointValue>.Failure(OperationalError.Protocol($"S7 读取失败: {ex.Message}")));
        }
    }

    public async Task<OperationResult<IReadOnlyList<RawPointValue>>> ReadBatchAsync(IEnumerable<DevicePoint> points, CancellationToken ct = default)
    {
        var results = new List<RawPointValue>();
        foreach (var p in points) { var r = await ReadAsync(p, ct); if (r.IsSuccess) results.Add(r.Value!); }
        return results;
    }

    public Task<OperationResult> WriteAsync(DevicePoint point, object value, CancellationToken ct = default)
    {
        if (_client is null) return Task.FromResult<OperationResult>(OperationalError.Unavailable("S7 未连接"));
        try { var r = _client.Write(FormatAddress(point.Address), Convert.ToSingle(value)); return Task.FromResult(r.IsSuccess ? OperationResult.Success() : (OperationResult)OperationalError.Protocol(r.Message)); }
        catch (Exception ex) { return Task.FromResult<OperationResult>(OperationalError.Protocol($"S7 写入失败: {ex.Message}")); }
    }

    public async Task<OperationResult> WriteBatchAsync(IEnumerable<KeyValuePair<DevicePoint, object>> entries, CancellationToken ct = default)
    {
        foreach (var (p, v) in entries) { var r = await WriteAsync(p, v, ct); if (r.IsFailure) return r; }
        return OperationResult.Success();
    }

    public void Dispose() { _client?.ConnectClose(); _client?.Dispose(); }

    private static string FormatAddress(string raw)
    {
        var a = S7AddressParser.Parse(raw);
        return a.DbNumber > 0 ? $"DB{a.DbNumber}.{a.VarType}{a.ByteOffset}" : $"{a.VarType}{a.ByteOffset}";
    }
}
