using HslCommunication.ModBus;
using Microsoft.Extensions.Logging;
using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Protocols;
using NitroGateway.Shared;

namespace NitroGateway.Protocols.Modbus;

/// <summary>Modbus TCP 驱动，基于 HslCommunication 实现。支持单点读写 + 批量合并读。</summary>
public sealed class ModbusTcpDriver : IProtocolDriver, IDisposable
{
    private readonly DeviceConnection _connection;
    private readonly ILogger _logger;
    private readonly ModbusAddressParser _addressParser = new();
    private readonly ModbusTcpNet _client = new();
    private readonly SemaphoreSlim _readLock = new(1, 1);
    private byte _unitId = 1;

    /// <summary>合并 Range 时允许的最大间隙（寄存器数）。≤此值则合并为一次读取</summary>
    private const int MaxMergeGap = 2;

    /// <summary>Modbus 单次最大寄存器数（协议限制，功能码 03/04 上限为 125）</summary>
    private const int MaxRegistersPerRequest = 125;

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
            if (
                parts.Length > 1 &&
                int.TryParse(parts[1], out var p) &&
                p > 0 &&
                p <= 65535)
            {
                _client.Port = p;
            }

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
        return Task.FromResult(TryReadSingle(point, addr));
    }

    /// <summary>
    /// 批量读取。策略：
    /// 1. 按功能区(Area)分组
    /// 2. 组内按地址排序
    /// 3. 连续地址（间隙 ≤2 寄存器）合并为一个 Range
    /// 4. 每个 Range 发一次 Modbus 多寄存器读指令
    /// 5. 从返回的字节流中按偏移量拆解出每个点位的值
    /// </summary>
    public async Task<OperationResult<IReadOnlyList<RawPointValue>>> ReadBatchAsync(
        IEnumerable<DevicePoint> points, CancellationToken ct = default)
    {
        await _readLock.WaitAsync(ct);
        try
        {
            if (State != DriverState.Connected)
                return OperationalError.Unavailable("Modbus 未连接");

            var pointList = points.ToList();
            if (pointList.Count == 0)
                return Array.Empty<RawPointValue>();

            // 1. 解析地址 + 寄存器数量
            var parsed = pointList.Select(p => new ParsedPoint(
                p, _addressParser.ParseWithCount(p.Address, p.DataType))).ToList();

            // 2. 合并成 Range
            var ranges = MergeRanges(parsed);

            // 3. 逐个 Range 批量读取
            var results = new List<RawPointValue>();
            foreach (var range in ranges)
            {
                var rangeResults = await ReadRangeAsync(range);
                results.AddRange(rangeResults);
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
                DataType.Float => _client.Write(ToHslAddress(addr), Convert.ToSingle(value)),
                DataType.Int16 => _client.Write(ToHslAddress(addr), Convert.ToInt16(value)),
                DataType.Bool => _client.Write(ToHslAddress(addr), Convert.ToBoolean(value)),
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

    // ═══════════ 内部类型 ═══════════

    private sealed record ParsedPoint(DevicePoint Point, ModbusAddress Addr);

    private sealed record ReadRange(
        ModbusArea Area,
        List<ParsedPoint> Points,
        ushort StartOffset,
        ushort TotalRegisters);

    // ═══════════ 单点读取 ═══════════

    private OperationResult<RawPointValue> TryReadSingle(DevicePoint point, ModbusAddress addr)
    {
        var address = ToHslAddress(addr);
        try
        {
            var value = point.DataType switch
            {
                DataType.Float => (object)_client.ReadFloat(address, 2).Content,
                DataType.Double => (object)_client.ReadDouble(address, 2).Content,
                DataType.Int16 => (object)_client.ReadInt16(address, 1).Content[0],
                DataType.UInt16 => (object)(ushort)_client.ReadInt16(address, 1).Content[0],
                DataType.Int32 => (object)_client.ReadInt32(address, 2).Content[0],
                DataType.UInt32 => (object)(uint)_client.ReadInt32(address, 2).Content[0],
                DataType.Bool => (object)_client.ReadBool(address, 1).Content[0],
                DataType.Byte => (object)(byte)_client.ReadInt16(address, 1).Content[0],
                DataType.Int64 => (object)_client.ReadInt64(address, 4).Content[0],
                DataType.UInt64 => (object)(ulong)_client.ReadInt64(address, 4).Content[0],
                DataType.String => (object)_client.ReadString(address, 10).Content,
                _ => (object)_client.ReadFloat(address, 2).Content
            };

            return OperationResult<RawPointValue>.Success(
                new RawPointValue { Point = point, Value = value, Timestamp = DateTime.UtcNow });
        }
        catch (Exception ex)
        {
            return OperationResult<RawPointValue>.Failure(
                OperationalError.Protocol($"读取失败: {ex.Message}"));
        }
    }

    // ═══════════ Range 合并 ═══════════

    /// <summary>
    /// 按功能区 → 地址排序 → 贪心合并连续 Range。
    /// 同一 Area 内，点 A 结尾 + 1 ± MaxMergeGap ≥ 点 B 开头 → 合并。
    /// </summary>
    private static List<ReadRange> MergeRanges(IReadOnlyList<ParsedPoint> parsed)
    {
        var ranges = new List<ReadRange>();

        foreach (var areaGroup in parsed.GroupBy(p => p.Addr.Area))
        {
            var sorted = areaGroup.OrderBy(p => p.Addr.Offset).ToList();
            if (sorted.Count == 0)
                continue;

            // Greedy merge
            var currentPoints = new List<ParsedPoint> { sorted[0] };
            var currentStart = sorted[0].Addr.Offset;
            var currentEnd = sorted[0].Addr.Offset + sorted[0].Addr.Count;

            for (var i = 1; i < sorted.Count; i++)
            {
                var point = sorted[i];
                var gap = (int)point.Addr.Offset - currentEnd;

                if (gap <= MaxMergeGap)
                {
                    // 可合并：扩展 range
                    currentPoints.Add(point);
                    currentEnd = Math.Max(currentEnd, point.Addr.Offset + point.Addr.Count);
                }
                else
                {
                    // 间隙过大：关闭当前 range，开新的
                    FlushRange();
                    currentPoints = new List<ParsedPoint> { point };
                    currentStart = point.Addr.Offset;
                    currentEnd = point.Addr.Offset + point.Addr.Count;
                }
            }
            FlushRange();

            void FlushRange()
            {
                var totalRegs = (ushort)Math.Min(currentEnd - currentStart, MaxRegistersPerRequest);
                ranges.Add(new ReadRange(areaGroup.Key, currentPoints, currentStart, totalRegs));
            }
        }

        return ranges;
    }

    // ═══════════ 批量 Range 读取 ═══════════

    /// <summary>
    /// Range 内按 DataType 分组，调 HSL 同类批量读方法。
    /// 同类型一次批量读，异类回退逐点。不自己解析字节。
    /// </summary>
    private async Task<List<RawPointValue>> ReadRangeAsync(ReadRange range)
    {
        var results = new List<RawPointValue>();

        foreach (var typeGroup in range.Points.GroupBy(p => p.Point.DataType))
        {
            try
            {
                var pts = typeGroup.ToList();
                var regsPerPoint = ModbusAddressParser.GetRegisterCount(typeGroup.Key);
                var totalRegs = pts.Count * regsPerPoint;

                if (totalRegs > MaxRegistersPerRequest)
                {
                    foreach (var pp in pts)
                    {
                        var s = TryReadSingle(pp.Point, pp.Addr);
                        if (s.IsSuccess) results.Add(s.Value!);
                    }
                    continue;
                }

                var hslAddr = ToHslAddress(range.Area, pts[0].Addr.Offset);
                var values = TryTypedBatch(hslAddr, typeGroup.Key, pts.Count);

                if (values is not null)
                {
                    for (var i = 0; i < pts.Count && i < values.Length; i++)
                        results.Add(new RawPointValue { Point = pts[i].Point, Value = values[i], Timestamp = DateTime.UtcNow });
                }
                else
                {
                    foreach (var pp in pts)
                    {
                        var s = TryReadSingle(pp.Point, pp.Addr);
                        if (s.IsSuccess) results.Add(s.Value!);
                    }
                }
            }
            catch
            {
                foreach (var pp in typeGroup)
                {
                    var s = TryReadSingle(pp.Point, pp.Addr);
                    if (s.IsSuccess) results.Add(s.Value!);
                }
            }
        }

        return results;
    }

    /// <summary>同类型批量读。失败返回 null。</summary>
    private object[]? TryTypedBatch(string address, DataType type, int count)
    {
        try
        {
            var c = (ushort)count;
            return type switch
            {
                DataType.Float   => _client.ReadFloat(address, c).Content?.Cast<object>().ToArray(),
                DataType.Int16   => _client.ReadInt16(address, c).Content?.Cast<object>().ToArray(),
                DataType.Int32   => _client.ReadInt32(address, c).Content?.Cast<object>().ToArray(),
                DataType.UInt16  => _client.ReadInt16(address, c).Content?.Select(v => (object)(ushort)v).ToArray(),
                DataType.UInt32  => _client.ReadInt32(address, c).Content?.Select(v => (object)(uint)v).ToArray(),
                DataType.Int64   => _client.ReadInt64(address, c).Content?.Cast<object>().ToArray(),
                DataType.UInt64  => _client.ReadInt64(address, c).Content?.Select(v => (object)(ulong)v).ToArray(),
                DataType.Double  => _client.ReadDouble(address, c).Content?.Cast<object>().ToArray(),
                _ => null
            };
        }
        catch { return null; }
    }

    // ═══════════ 地址转换 ═══════════

    /// <summary>ModbusAddress → HSL 地址字符串</summary>
    private static string ToHslAddress(ModbusAddress a) => ToHslAddress(a.Area, a.Offset);

    /// <summary>Area + Offset → HSL 地址字符串</summary>
    private static string ToHslAddress(ModbusArea area, ushort offset) => area switch
    {
        ModbusArea.InputRegister => $"x=4;{offset}",
        ModbusArea.Coil => $"x=1;{offset}",
        ModbusArea.DiscreteInput => $"x=2;{offset}",
        _ => offset.ToString()    // HoldingRegister: 直接用数字
    };
}
