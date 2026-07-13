using Microsoft.Extensions.Logging;
using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Protocols;
using NitroGateway.Shared;
using S7Net = S7.Net;

namespace NitroGateway.Protocols.S7;

/// <summary>Siemens S7 协议驱动，基于 S7netplus 实现。地址格式: DB1.DBD0, DB10.DBW2</summary>
public sealed class S7Driver : IProtocolDriver, IDisposable
{
    private readonly DeviceConnection _connection;
    private readonly ILogger _logger;
    private S7Net.Plc? _plc;

    /// <inheritdoc />
    public DriverState State { get; private set; } = DriverState.Disconnected;

    /// <inheritdoc />
    public DriverCapability Capability => S7DriverCapability.Instance;

    public S7Driver(DeviceConnection connection, ILogger logger)
    {
        _connection = connection;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<OperationResult> ConnectAsync(CancellationToken ct = default)
    {
        if (State == DriverState.Connected) return Task.FromResult(OperationResult.Success());
        State = DriverState.Connecting;

        try
        {
            var parts = _connection.Endpoint.Split(':');
            var ip = parts[0];
            var port = parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : 102;
            var rack = (short)(int)(_connection.Parameters.GetValueOrDefault("Rack") ?? 0);
            var slot = (short)(int)(_connection.Parameters.GetValueOrDefault("Slot") ?? 1);

            var cpuType = _connection.Parameters.GetValueOrDefault("CpuType") switch
            {
                "S7-1200" => S7Net.CpuType.S71200,
                "S7-1500" => S7Net.CpuType.S71500,
                "S7-300"  => S7Net.CpuType.S7300,
                "S7-400"  => S7Net.CpuType.S7400,
                _         => S7Net.CpuType.S71200
            };

            _plc = new S7Net.Plc(cpuType, ip, port, rack, slot);
            _plc.Open();
            State = DriverState.Connected;
            _logger.LogInformation("S7 已连接: {Endpoint}", _connection.Endpoint);
            return Task.FromResult(OperationResult.Success());
        }
        catch (Exception ex)
        {
            State = DriverState.Faulted;
            return Task.FromResult<OperationResult>(OperationalError.Timeout($"S7 连接失败: {ex.Message}"));
        }
    }

    /// <inheritdoc />
    public Task<OperationResult> DisconnectAsync(CancellationToken ct = default)
    {
        try { _plc?.Close(); } catch { }
        _plc = null; State = DriverState.Disconnected;
        return Task.FromResult(OperationResult.Success());
    }

    /// <inheritdoc />
    public async Task<OperationResult> PingAsync(CancellationToken ct = default)
    {
        if (_plc is null) return OperationalError.Unavailable("S7 未连接");
        try { await _plc.ReadBytesAsync(S7Net.DataType.DataBlock, 1, 0, 1, ct); return OperationResult.Success(); }
        catch (Exception ex) { return OperationalError.Timeout($"S7 Ping 失败: {ex.Message}"); }
    }

    /// <inheritdoc />
    public async Task<OperationResult<RawPointValue>> ReadAsync(DevicePoint point, CancellationToken ct = default)
    {
        var result = await ReadBatchAsync([point], ct);
        if (result.IsFailure) return result.Error!;
        var first = result.Value!.FirstOrDefault();
        return first is not null ? OperationResult<RawPointValue>.Success(first) : OperationalError.Protocol("读取失败: 无数据");
    }

    /// <inheritdoc />
    public async Task<OperationResult<IReadOnlyList<RawPointValue>>> ReadBatchAsync(
        IEnumerable<DevicePoint> points, CancellationToken ct = default)
    {
        if (_plc is null || State != DriverState.Connected)
            return OperationalError.Unavailable("S7 未连接");

        var list = points.ToList();
        if (list.Count == 0) return Array.Empty<RawPointValue>();
        var results = new List<RawPointValue>();

        try
        {
            foreach (var p in list)
            {
                var addr = S7AddressParser.Parse(p.Address);
                var bytes = p.DataType switch
                {
                    DataType.Float or DataType.Int32 or DataType.UInt32 => 4,
                    DataType.Int16 or DataType.UInt16 => 2,
                    DataType.Double or DataType.Int64 or DataType.UInt64 => 8,
                    DataType.Bool or DataType.Byte => 1,
                    _ => 4
                };

                var raw = await _plc.ReadBytesAsync(
                    S7Net.DataType.DataBlock, addr.DbNumber, addr.ByteOffset, bytes, ct);

                var value = Decode(p.DataType, raw);
                results.Add(new RawPointValue { Point = p, Value = value, Timestamp = DateTime.UtcNow });
            }
        }
        catch (Exception ex) { return OperationalError.Protocol($"S7 读取失败: {ex.Message}"); }

        return results;
    }

    /// <inheritdoc />
    public async Task<OperationResult> WriteAsync(DevicePoint point, object value, CancellationToken ct = default)
    {
        if (_plc is null) return OperationalError.Unavailable("S7 未连接");

        try
        {
            var addr = S7AddressParser.Parse(point.Address);
            if (point.DataType == DataType.Bool)
            {
                await _plc.WriteAsync($"DB{addr.DbNumber}.DBX{addr.ByteOffset}.{addr.BitOffset}",
                    Convert.ToBoolean(value));
            }
            else
            {
                var bytes = Encode(point.DataType, value);
                await _plc.WriteAsync(S7Net.DataType.DataBlock, addr.DbNumber, addr.ByteOffset, bytes);
            }
            return OperationResult.Success();
        }
        catch (Exception ex) { return OperationalError.Protocol($"S7 写入失败: {ex.Message}"); }
    }

    /// <inheritdoc />
    public async Task<OperationResult> WriteBatchAsync(
        IEnumerable<KeyValuePair<DevicePoint, object>> entries, CancellationToken ct = default)
    {
        foreach (var (p, v) in entries) { var r = await WriteAsync(p, v, ct); if (r.IsFailure) return r; }
        return OperationResult.Success();
    }

    public void Dispose() { _plc?.Close(); }

    // ── 类型编解码 ──

    private static object Decode(DataType type, byte[] bytes)
    {
        return type switch
        {
            DataType.Int16  => BitConverter.ToInt16(BigEndian(bytes, 2), 0),
            DataType.UInt16 => BitConverter.ToUInt16(BigEndian(bytes, 2), 0),
            DataType.Int32  => BitConverter.ToInt32(BigEndian(bytes, 4), 0),
            DataType.UInt32 => BitConverter.ToUInt32(BigEndian(bytes, 4), 0),
            DataType.Float  => BitConverter.ToSingle(BigEndian(bytes, 4), 0),
            DataType.Double => BitConverter.ToDouble(BigEndian(bytes, 8), 0),
            DataType.Bool   => bytes[0] != 0,
            DataType.Byte   => bytes[0],
            DataType.String => System.Text.Encoding.ASCII.GetString(bytes).TrimEnd('\0'),
            _ => 0f
        };
    }

    private static byte[] Encode(DataType type, object value)
    {
        var bytes = type switch
        {
            DataType.Int16  => BitConverter.GetBytes(Convert.ToInt16(value)),
            DataType.UInt16 => BitConverter.GetBytes(Convert.ToUInt16(value)),
            DataType.Int32  => BitConverter.GetBytes(Convert.ToInt32(value)),
            DataType.UInt32 => BitConverter.GetBytes(Convert.ToUInt32(value)),
            DataType.Float  => BitConverter.GetBytes(Convert.ToSingle(value)),
            DataType.Double => BitConverter.GetBytes(Convert.ToDouble(value)),
            DataType.Byte   => new[] { Convert.ToByte(value) },
            DataType.String => System.Text.Encoding.ASCII.GetBytes((string)value + '\0'),
            _ => BitConverter.GetBytes(Convert.ToSingle(value))
        };
        return BigEndian(bytes, bytes.Length);
    }

    /// <summary>S7 使用 Big-Endian。x86 使用 Little-Endian，需转换</summary>
    private static byte[] BigEndian(byte[] bytes, int count)
    {
        if (BitConverter.IsLittleEndian && count > 1)
            Array.Reverse(bytes, 0, count);
        return bytes[..count];
    }
}
