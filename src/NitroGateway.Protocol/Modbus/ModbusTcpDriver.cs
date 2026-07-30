using HslCommunication.ModBus;
using Microsoft.Extensions.Logging;
using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Protocols;
using NitroGateway.Shared;

namespace NitroGateway.Protocols.Modbus;

/// <summary>Modbus TCP 驱动，基于 HslCommunication 实现</summary>
public sealed class ModbusTcpDriver : IProtocolDriver, IDisposable
{
    private readonly DeviceConnection _connection;
    private readonly ILogger _logger;
    private readonly ModbusAddressParser _addressParser = new();
    private readonly ModbusTcpNet _client = new();
    private readonly SemaphoreSlim _readLock = new(1, 1);
    private byte _unitId = 1;

    public DriverState State { get; private set; } = DriverState.Disconnected;
    public DriverCapability Capability => ModbusDriverCapability.Instance;

    public ModbusTcpDriver(DeviceConnection connection, ILogger logger)
    {
        _connection = connection;
        _logger = logger;
        _unitId = (byte)(int)(connection.Parameters.GetValueOrDefault("UnitId") ?? 1);

        _client.Station = _unitId;
        _client.ConnectTimeOut = connection.ConnectTimeoutMs;
        _client.ReceiveTimeOut = connection.RequestTimeoutMs;
    }

    public Task<OperationResult> ConnectAsync(CancellationToken ct = default)
    {
        if (State == DriverState.Connected)
            return Task.FromResult(OperationResult.Success());

        State = DriverState.Connecting;

        try
        {
            var parts = _connection.Endpoint.Split(':');
            _client.IpAddress = parts[0];
            _client.Port = parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : 502;

            var result = _client.ConnectServer();
            if (result.IsSuccess)
            {
                State = DriverState.Connected;
                _logger.LogInformation("Modbus 连接成功: {Endpoint}", _connection.Endpoint);
                return Task.FromResult(OperationResult.Success());
            }

            State = DriverState.Faulted;
            return Task.FromResult<OperationResult>(OperationalError.Timeout($"Modbus 连接失败: {result.Message}"));
        }
        catch (Exception ex)
        {
            State = DriverState.Faulted;
            return Task.FromResult<OperationResult>(OperationalError.Timeout($"Modbus 连接异常: {ex.Message}"));
        }
    }

    public Task<OperationResult> DisconnectAsync(CancellationToken ct = default)
    {
        try { _client.ConnectClose(); } catch { }
        State = DriverState.Disconnected;
        return Task.FromResult(OperationResult.Success());
    }

    public Task<OperationResult> PingAsync(CancellationToken ct = default)
    {
        if (State != DriverState.Connected)
            return Task.FromResult<OperationResult>(OperationalError.Unavailable("Modbus 未连接"));

        try
        {
            var r = _client.ReadInt16("0", 1);
            return Task.FromResult(r.IsSuccess ? OperationResult.Success() : (OperationResult)OperationalError.Timeout(r.Message));
        }
        catch (Exception ex)
        {
            return Task.FromResult<OperationResult>(OperationalError.Timeout($"Ping 失败: {ex.Message}"));
        }
    }

    public Task<OperationResult<RawPointValue>> ReadAsync(DevicePoint point, CancellationToken ct = default)
    {
        var addr = _addressParser.ParseWithCount(point.Address, point.DataType);
        var address = ToHslAddress(addr);

        try
        {
            var value = point.DataType switch
            {
                DataType.Float   => (object)_client.ReadFloat(address, 2).Content,
                DataType.Double  => (object)_client.ReadDouble(address, 2).Content,
                DataType.Int16   => (object)_client.ReadInt16(address, 1).Content[0],
                DataType.UInt16  => (object)(ushort)_client.ReadInt16(address, 1).Content[0],
                DataType.Int32   => (object)_client.ReadInt32(address, 2).Content[0],
                DataType.UInt32  => (object)(uint)_client.ReadInt32(address, 2).Content[0],
                DataType.Bool    => (object)_client.ReadBool(address, 1).Content[0],
                DataType.Byte    => (object)(byte)_client.ReadInt16(address, 1).Content[0],
                DataType.Int64   => (object)_client.ReadInt64(address, 4).Content[0],
                DataType.UInt64  => (object)(ulong)_client.ReadInt64(address, 4).Content[0],
                DataType.String  => (object)_client.ReadString(address, 10).Content,
                _ => (object)_client.ReadFloat(address, 2).Content
            };

            var raw = new RawPointValue { Point = point, Value = value, Timestamp = DateTime.UtcNow };
            return Task.FromResult(OperationResult<RawPointValue>.Success(raw));
        }
        catch (Exception ex)
        {
            return Task.FromResult(OperationResult<RawPointValue>.Failure(OperationalError.Protocol($"读取失败: {ex.Message}")));
        }
    }

    public async Task<OperationResult<IReadOnlyList<RawPointValue>>> ReadBatchAsync(
        IEnumerable<DevicePoint> points, CancellationToken ct = default)
    {
        await _readLock.WaitAsync(ct);
        try
        {
            if (State != DriverState.Connected)
                return OperationalError.Unavailable("Modbus 未连接");

            var results = new List<RawPointValue>();
            foreach (var p in points)
            {
                var r = await ReadAsync(p, ct);
                if (r.IsSuccess) results.Add(r.Value!);
            }
            return results;
        }
        catch (Exception ex)
        {
            return OperationalError.Protocol($"批量读取失败: {ex.Message}");
        }
        finally
        {
            _readLock.Release();
        }
    }

    public Task<OperationResult> WriteAsync(DevicePoint point, object value, CancellationToken ct = default)
    {
        if (State != DriverState.Connected)
            return Task.FromResult<OperationResult>(OperationalError.Unavailable("Modbus 未连接"));

        var addr = _addressParser.ParseWithCount(point.Address, point.DataType);

        try
        {
            var result = point.DataType switch
            {
                DataType.Float   => _client.Write(ToHslAddress(addr), Convert.ToSingle(value)),
                DataType.Int16   => _client.Write(ToHslAddress(addr), Convert.ToInt16(value)),
                DataType.Bool    => _client.Write(ToHslAddress(addr), Convert.ToBoolean(value)),
                _ => _client.Write(ToHslAddress(addr), Convert.ToSingle(value))
            };

            return Task.FromResult(result.IsSuccess ? OperationResult.Success() : (OperationResult)OperationalError.Protocol(result.Message));
        }
        catch (Exception ex)
        {
            return Task.FromResult<OperationResult>(OperationalError.Protocol($"写入失败: {ex.Message}"));
        }
    }

    public async Task<OperationResult> WriteBatchAsync(
        IEnumerable<KeyValuePair<DevicePoint, object>> entries, CancellationToken ct = default)
    {
        foreach (var (p, v) in entries)
        {
            var r = await WriteAsync(p, v, ct);
            if (r.IsFailure) return r;
        }
        return OperationResult.Success();
    }

    public void Dispose() { _client.ConnectClose(); _client.Dispose(); }

    /// <summary>
    /// ModbusAddress → Hsl 地址字符串。
    /// HoldingRegister: "offset", InputRegister: "x=4;offset", Coil: "x=1;offset"
    /// </summary>
    private static string ToHslAddress(ModbusAddress a) => a.Area switch
    {
        ModbusArea.InputRegister  => $"x=4;{a.Offset}",
        ModbusArea.Coil           => $"x=1;{a.Offset}",
        ModbusArea.DiscreteInput  => $"x=2;{a.Offset}",
        _ => a.Offset.ToString()
    };
}
