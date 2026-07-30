using HslCommunication.Profinet.Melsec;
using Microsoft.Extensions.Logging;
using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Protocols;
using NitroGateway.Shared;

namespace NitroGateway.Protocols.Mitsubishi;

/// <summary>三菱 MC 协议驱动，基于 HslCommunication。地址: D100, M200, X0, Y10</summary>
public sealed class MitsubishiDriver : IProtocolDriver, IDisposable
{
    private readonly DeviceConnection _connection;
    private readonly ILogger _logger;
    private MelsecMcNet? _client;

    public DriverState State { get; private set; } = DriverState.Disconnected;
    public DriverCapability Capability => MitsubishiDriverCapability.Instance;

    public MitsubishiDriver(DeviceConnection connection, ILogger logger)
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
            var port = parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : 6000;

            _client = new MelsecMcNet(ip, port);
            var r = _client.ConnectServer();
            if (r.IsSuccess) { State = DriverState.Connected; return Task.FromResult(OperationResult.Success()); }

            State = DriverState.Faulted;
            return Task.FromResult<OperationResult>(OperationalError.Timeout($"三菱 MC 连接失败: {r.Message}"));
        }
        catch (Exception ex)
        {
            State = DriverState.Faulted;
            return Task.FromResult<OperationResult>(OperationalError.Timeout($"三菱 MC 连接异常: {ex.Message}"));
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
        if (_client is null) return Task.FromResult<OperationResult>(OperationalError.Unavailable("MC 未连接"));
        try { var r = _client.ReadInt16("D0"); return Task.FromResult(r.IsSuccess ? OperationResult.Success() : (OperationResult)OperationalError.Timeout(r.Message)); }
        catch (Exception ex) { return Task.FromResult<OperationResult>(OperationalError.Timeout($"Ping 失败: {ex.Message}")); }
    }

    public Task<OperationResult<RawPointValue>> ReadAsync(DevicePoint point, CancellationToken ct = default)
    {
        if (_client is null) return Task.FromResult(OperationResult<RawPointValue>.Failure(OperationalError.Unavailable("MC 未连接")));

        try
        {
            var addr = point.Address; // 直接使用地址字符串: "D100", "M200"
            var value = point.DataType switch
            {
                DataType.Float   => (object)_client.ReadFloat(addr).Content,
                DataType.Int16   => (object)_client.ReadInt16(addr).Content,
                DataType.Int32   => (object)_client.ReadInt32(addr).Content,
                DataType.Bool    => (object)_client.ReadBool(addr).Content,
                DataType.Double  => (object)_client.ReadDouble(addr).Content,
                _ => (object)_client.ReadFloat(addr).Content
            };

            var raw = new RawPointValue { Point = point, Value = value, Timestamp = DateTime.UtcNow };
            return Task.FromResult(OperationResult<RawPointValue>.Success(raw));
        }
        catch (Exception ex)
        {
            return Task.FromResult(OperationResult<RawPointValue>.Failure(OperationalError.Protocol($"MC 读取失败: {ex.Message}")));
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
        if (_client is null) return Task.FromResult<OperationResult>(OperationalError.Unavailable("MC 未连接"));
        try { var r = _client.Write(point.Address, Convert.ToSingle(value)); return Task.FromResult(r.IsSuccess ? OperationResult.Success() : (OperationResult)OperationalError.Protocol(r.Message)); }
        catch (Exception ex) { return Task.FromResult<OperationResult>(OperationalError.Protocol($"MC 写入失败: {ex.Message}")); }
    }

    public async Task<OperationResult> WriteBatchAsync(IEnumerable<KeyValuePair<DevicePoint, object>> entries, CancellationToken ct = default)
    {
        foreach (var (p, v) in entries) { var r = await WriteAsync(p, v, ct); if (r.IsFailure) return r; }
        return OperationResult.Success();
    }

    public void Dispose() { _client?.ConnectClose(); _client?.Dispose(); }
}
