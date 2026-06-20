using System.IO.Ports;
using FluentModbus;
using Microsoft.Extensions.Logging;
using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Protocols;
using NitroGateway.Shared;

namespace NitroGateway.Protocol.Modbus;

/// <summary>
/// Modbus RTU 协议驱动，基于 FluentModbus ModbusRtuClient。
/// 通过串口（COM3/RS-485）通信，不使用 TCP。
/// </summary>
public sealed class ModbusRtuDriver : IProtocolDriver, IDisposable
{
    private readonly DeviceConnection _connection;
    private readonly ILogger<ModbusRtuDriver> _logger;
    private readonly ModbusAddressParser _addressParser = new();
    private readonly ModbusRtuClient _client = new();
    private byte _unitId = 1;
    private string _port = "COM1";
    private int _baudRate = 9600;
    private Parity _parity = Parity.None;
    private int _dataBits = 8;
    private StopBits _stopBits = StopBits.One;

    /// <inheritdoc />
    public DriverState State { get; private set; } = DriverState.Disconnected;

    /// <inheritdoc />
    public DriverCapability Capability => ModbusDriverCapability.Instance;

    /// <summary>创建 Modbus RTU 驱动实例</summary>
    public ModbusRtuDriver(DeviceConnection connection, ILogger<ModbusRtuDriver> logger)
    {
        _connection = connection;
        _logger = logger;

        _unitId = (byte)(int)(connection.Parameters.GetValueOrDefault("UnitId") ?? 1);
        _port = connection.Parameters.GetValueOrDefault("Port")?.ToString()
                ?? connection.Endpoint.Split(':')[0];
        _baudRate = (int)(connection.Parameters.GetValueOrDefault("BaudRate") ?? 9600);
        _dataBits = (int)(connection.Parameters.GetValueOrDefault("DataBits") ?? 8);

        _parity = connection.Parameters.GetValueOrDefault("Parity")?.ToString()?.ToUpperInvariant() switch
        {
            "EVEN" => Parity.Even,
            "ODD" => Parity.Odd,
            "MARK" => Parity.Mark,
            "SPACE" => Parity.Space,
            _ => Parity.None
        };

        _stopBits = connection.Parameters.GetValueOrDefault("StopBits")?.ToString()?.ToUpperInvariant() switch
        {
            "TWO" or "2" => StopBits.Two,
            "ONEPOINTFIVE" or "1.5" => StopBits.OnePointFive,
            _ => StopBits.One
        };

        _client.BaudRate = _baudRate;
        _client.Parity = _parity;
        _client.StopBits = _stopBits;
        _client.ReadTimeout = connection.RequestTimeoutMs;
        _client.WriteTimeout = connection.RequestTimeoutMs;
    }

    /// <inheritdoc />
    public Task<OperationResult> ConnectAsync(CancellationToken ct = default)
    {
        if (State == DriverState.Connected) return Task.FromResult(OperationResult.Success());
        State = DriverState.Connecting;

        try
        {
            _client.Connect(_port);
            State = DriverState.Connected;

            var parityStr = _parity switch
            {
                Parity.None => "N", Parity.Even => "E", Parity.Odd => "O",
                Parity.Mark => "M", Parity.Space => "S", _ => "N"
            };
            var stopStr = _stopBits switch
            {
                StopBits.One => "1", StopBits.Two => "2", StopBits.OnePointFive => "1.5", _ => "1"
            };

            _logger.LogInformation("Modbus RTU 已连接: {Port} {Baud}{Parity}{Bits}{Stop} UnitId={UnitId}",
                _port, _baudRate, parityStr, _dataBits, stopStr, _unitId);

            return Task.FromResult(OperationResult.Success());
        }
        catch (Exception ex)
        {
            State = DriverState.Faulted;
            _logger.LogError(ex, "Modbus RTU 连接失败: {Port}", _port);
            return Task.FromResult<OperationResult>(OperationalError.Timeout($"RTU 连接失败 {_port}: {ex.Message}"));
        }
    }

    /// <inheritdoc />
    public Task<OperationResult> DisconnectAsync(CancellationToken ct = default)
    {
        try { _client.Close(); } catch { }
        State = DriverState.Disconnected;
        return Task.FromResult(OperationResult.Success());
    }

    /// <inheritdoc />
    public Task<OperationResult> PingAsync(CancellationToken ct = default)
    {
        if (!_client.IsConnected) return Task.FromResult<OperationResult>(OperationalError.Unavailable("RTU 未连接"));
        try { _client.ReadHoldingRegisters(_unitId, 0, 1); return Task.FromResult(OperationResult.Success()); }
        catch (Exception ex) { return Task.FromResult<OperationResult>(OperationalError.Timeout($"Ping 失败: {ex.Message}")); }
    }

    /// <inheritdoc />
    public async Task<OperationResult<RawPointValue>> ReadAsync(DevicePoint point, CancellationToken ct = default)
    {
        var result = await ReadBatchAsync([point], ct);
        if (result.IsFailure) return result.Error!;
        var first = result.Value!.FirstOrDefault();
        return first is not null
            ? OperationResult<RawPointValue>.Success(first)
            : OperationalError.Protocol($"读取失败: {point.Name}");
    }

    /// <inheritdoc />
    public Task<OperationResult<IReadOnlyList<RawPointValue>>> ReadBatchAsync(
        IEnumerable<DevicePoint> points, CancellationToken ct = default)
    {
        if (!_client.IsConnected || State != DriverState.Connected)
            return Task.FromResult<OperationResult<IReadOnlyList<RawPointValue>>>(
                OperationalError.Unavailable("RTU 未连接"));

        var pointList = points.ToList();
        if (pointList.Count == 0)
            return Task.FromResult<OperationResult<IReadOnlyList<RawPointValue>>>(
                Array.Empty<RawPointValue>());

        // RTU 不支持异步，用 Task.Run 适配接口
        return Task.Run(() =>
        {
            var groups = GroupByContinuity(pointList);
            var results = new List<RawPointValue>();
            var timestamp = DateTime.UtcNow;

            foreach (var group in groups)
            {
                var groupList = group.ToList();
                var firstAddr = _addressParser.ParseWithCount(groupList[0].Address, groupList[0].DataType);
                var lastAddr = _addressParser.ParseWithCount(groupList[^1].Address, groupList[^1].DataType);
                var totalCount = (ushort)(lastAddr.Offset + lastAddr.Count - firstAddr.Offset);

                try
                {
                    if (firstAddr.Area is ModbusArea.Coil or ModbusArea.DiscreteInput)
                    {
                        var coilBytes = firstAddr.Area == ModbusArea.Coil
                            ? _client.ReadCoils(_unitId, firstAddr.Offset, totalCount)
                            : _client.ReadDiscreteInputs(_unitId, firstAddr.Offset, totalCount);

                        for (var i = 0; i < groupList.Count && i < coilBytes.Length; i++)
                            results.Add(new RawPointValue { Point = groupList[i], Value = coilBytes[i] != 0, Timestamp = timestamp });
                    }
                    else
                    {
                        Span<byte> rawBytes = firstAddr.Area switch
                        {
                            ModbusArea.HoldingRegister => _client.ReadHoldingRegisters(_unitId, firstAddr.Offset, totalCount),
                            ModbusArea.InputRegister => _client.ReadInputRegisters(_unitId, firstAddr.Offset, totalCount),
                            _ => throw new NotSupportedException($"不支持: {firstAddr.Area}")
                        };

                        var byteOffset = 0;
                        foreach (var point in groupList)
                        {
                            var addr = _addressParser.ParseWithCount(point.Address, point.DataType);
                            var segment = new ushort[addr.Count];
                            for (var i = 0; i < addr.Count && byteOffset + (i * 2) + 1 < rawBytes.Length; i++)
                                segment[i] = (ushort)((rawBytes[byteOffset + i * 2] << 8) | rawBytes[byteOffset + i * 2 + 1]);
                            byteOffset += addr.Count * 2;
                            results.Add(new RawPointValue { Point = point, Value = DecodeRegisters(segment, point.DataType), Timestamp = timestamp });
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "RTU 读失败: Area={Area} Offset={Offset}", firstAddr.Area, firstAddr.Offset);
                }
            }
            return OperationResult<IReadOnlyList<RawPointValue>>.Success(results);
        }, ct);
    }

    /// <inheritdoc />
    public Task<OperationResult> WriteAsync(DevicePoint point, object value, CancellationToken ct = default)
    {
        if (!_client.IsConnected) return Task.FromResult<OperationResult>(OperationalError.Unavailable("RTU 未连接"));
        return Task.Run(() =>
        {
            var ma = _addressParser.ParseWithCount(point.Address, point.DataType);
            try
            {
                if (ma.Area == ModbusArea.HoldingRegister)
                {
                    var regs = EncodeValue(value, point.DataType);
                    if (regs.Length == 1) _client.WriteSingleRegister(_unitId, ma.Offset, (short)regs[0]);
                    else _client.WriteMultipleRegisters(_unitId, ma.Offset, regs);
                }
                else if (ma.Area == ModbusArea.Coil)
                    _client.WriteSingleCoil(_unitId, ma.Offset, Convert.ToBoolean(value));
                else return OperationResult.Failure(OperationalError.Protocol($"不支持写入: {ma.Area}"));
                return OperationResult.Success();
            }
            catch (Exception ex) { return OperationalError.Protocol($"写入失败: {ex.Message}"); }
        }, ct);
    }

    /// <inheritdoc />
    public async Task<OperationResult> WriteBatchAsync(
        IEnumerable<KeyValuePair<DevicePoint, object>> entries, CancellationToken ct = default)
    {
        foreach (var (p, v) in entries) { var r = await WriteAsync(p, v, ct); if (r.IsFailure) return r; }
        return OperationResult.Success();
    }

    public void Dispose() { _client.Dispose(); }

    // ═══════════ 复用逻辑（与 ModbusTcpDriver 相同） ═══════════

    private static IEnumerable<IEnumerable<DevicePoint>> GroupByContinuity(List<DevicePoint> points)
    {
        var parser = new ModbusAddressParser();
        var sorted = points.Select(p => (Point: p, Addr: parser.ParseWithCount(p.Address, p.DataType)))
            .OrderBy(x => x.Addr.Area).ThenBy(x => x.Addr.Offset).ToList();
        var groups = new List<List<DevicePoint>>();
        List<DevicePoint>? cur = null; ModbusAddress? last = null;
        foreach (var (p, a) in sorted)
        {
            if (cur is null || last is null || a.Area != last.Area || a.Offset > last.Offset + last.Count) { cur = []; groups.Add(cur); }
            cur.Add(p); last = a;
        }
        return groups;
    }

    private static ushort[] EncodeValue(object value, DataType type) => type switch
    {
        DataType.Bool => [(ushort)(Convert.ToBoolean(value) ? 1 : 0)],
        DataType.Byte => [Convert.ToByte(value)],
        DataType.Int16 => [(ushort)Convert.ToInt16(value)],
        DataType.UInt16 => [Convert.ToUInt16(value)],
        DataType.Int32 => ToRegs(Convert.ToInt32(value)),
        DataType.UInt32 => ToRegs(Convert.ToUInt32(value)),
        DataType.Float => ToRegs(Convert.ToSingle(value)),
        DataType.Int64 => ToRegs(Convert.ToInt64(value)),
        DataType.UInt64 => ToRegs(Convert.ToUInt64(value)),
        DataType.Double => ToRegs(Convert.ToDouble(value)),
        _ => throw new NotSupportedException($"DataType: {type}")
    };

    private static object DecodeRegisters(ushort[] regs, DataType type)
    {
        if (regs.Length == 0) return 0;
        var bytes = new byte[regs.Length * 2];
        for (var i = 0; i < regs.Length; i++) { bytes[i * 2] = (byte)(regs[i] >> 8); bytes[i * 2 + 1] = (byte)(regs[i] & 0xFF); }
        if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
        return type switch
        {
            DataType.Bool => regs[0] != 0, DataType.Byte => bytes[0],
            DataType.Int16 => (short)regs[0], DataType.UInt16 => regs[0],
            DataType.Int32 when bytes.Length >= 4 => BitConverter.ToInt32(bytes),
            DataType.UInt32 when bytes.Length >= 4 => BitConverter.ToUInt32(bytes),
            DataType.Float when bytes.Length >= 4 => (double)BitConverter.ToSingle(bytes),
            DataType.Int64 when bytes.Length >= 8 => BitConverter.ToInt64(bytes),
            DataType.UInt64 when bytes.Length >= 8 => BitConverter.ToUInt64(bytes),
            DataType.Double when bytes.Length >= 8 => BitConverter.ToDouble(bytes),
            DataType.String => System.Text.Encoding.ASCII.GetString(bytes).TrimEnd('\0'),
            _ => regs
        };
    }

    private static ushort[] ToRegs(float f) { var b = BitConverter.GetBytes(f); if (BitConverter.IsLittleEndian) Array.Reverse(b); return [(ushort)((b[0] << 8) | b[1]), (ushort)((b[2] << 8) | b[3])]; }
    private static ushort[] ToRegs(int v) => ToRegs((float)v);
    private static ushort[] ToRegs(uint v) => ToRegs((float)v);
    private static ushort[] ToRegs(double d) { var b = BitConverter.GetBytes(d); if (BitConverter.IsLittleEndian) Array.Reverse(b); return [(ushort)((b[0] << 8) | b[1]), (ushort)((b[2] << 8) | b[3]), (ushort)((b[4] << 8) | b[4]), (ushort)((b[6] << 8) | b[7])]; }
    private static ushort[] ToRegs(long v) => ToRegs((double)v);
    private static ushort[] ToRegs(ulong v) => ToRegs((double)v);
}
