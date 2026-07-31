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
                var totalRegs = (ushort)(currentEnd - currentStart);
                ranges.Add(new ReadRange(areaGroup.Key, currentPoints, currentStart, totalRegs));
            }
        }

        return ranges;
    }

    // ═══════════ 批量 Range 读取 ═══════════

    /// <summary>对单个 Range 发一次多寄存器读，然后拆解字节流</summary>
    private async Task<List<RawPointValue>> ReadRangeAsync(ReadRange range)
    {
        var address = ToHslAddress(range.Area, range.StartOffset);
        var results = new List<RawPointValue>();

        try
        {
            // 一次读取 range.TotalRegisters 个寄存器
            var readResult = await _client.ReadAsync(address, range.TotalRegisters);
            if (!readResult.IsSuccess)
            {
                // Range 读取失败 → 回退到逐点读取
                _logger.LogDebug(
                    "Modbus Range 读取失败 [{Start} +{Count}]: {Error}，回退逐点读取",
                    address, range.TotalRegisters, readResult.Message);

                foreach (var pp in range.Points)
                {
                    var single = TryReadSingle(pp.Point, pp.Addr);
                    if (single.IsSuccess) results.Add(single.Value!);
                }
                return results;
            }

            var bytes = readResult.Content;
            // bytes 格式: [reg0_hi, reg0_lo, reg1_hi, reg1_lo, ...]
            // 每个寄存器 2 字节，总共 totalRegisters * 2 字节

            foreach (var pp in range.Points)
            {
                var byteOffset = (pp.Addr.Offset - range.StartOffset) * 2;
                var value = BytesToValue(bytes, byteOffset, pp.Point.DataType);

                results.Add(new RawPointValue
                {
                    Point = pp.Point,
                    Value = value,
                    Timestamp = DateTime.UtcNow
                });
            }
        }
        catch (Exception ex)
        {
            // Range 异常 → 回退逐点
            _logger.LogDebug("Modbus Range 读取异常: {Error}，回退逐点", ex.Message);
            foreach (var pp in range.Points)
            {
                var single = TryReadSingle(pp.Point, pp.Addr);
                if (single.IsSuccess) results.Add(single.Value!);
            }
        }

        return results;
    }

    // ═══════════ 字节 → 值 ═══════════

    /// <summary>从字节流指定偏移量读取一个 Modbus 值。大端序。</summary>
    private static object? BytesToValue(byte[] bytes, int offset, DataType dataType)
    {
        return dataType switch
        {
            DataType.Int16 => (object)(short)((bytes[offset] << 8) | bytes[offset + 1]),
            DataType.UInt16 => (object)(ushort)((bytes[offset] << 8) | bytes[offset + 1]),
            DataType.Int32 => ReadBigEndianInt32(bytes, offset),
            DataType.UInt32 => (object)(uint)ReadBigEndianInt32(bytes, offset),
            DataType.Float => ReadBigEndianFloat(bytes, offset),
            DataType.Int64 => ReadBigEndianInt64(bytes, offset),
            DataType.UInt64 => (object)(ulong)ReadBigEndianInt64(bytes, offset),
            DataType.Double => ReadBigEndianDouble(bytes, offset),
            DataType.Bool => (object)(bytes[offset + 1] != 0),  // 线圈：低字节有效
            DataType.Byte => (object)bytes[offset + 1],
            _ => ReadBigEndianFloat(bytes, offset)
        };
    }

    // 注：Modbus 寄存器大端序。将两个寄存器的 4 字节排列为 [hi_hi, hi_lo, lo_hi, lo_lo]，
    // 即 byte[offset] = 高位的高字节, byte[offset+3] = 低位的低字节。
    // BitConverter 在小端机器上读出来是反的，需要翻转。

    private static float ReadBigEndianFloat(byte[] bytes, int offset)
    {
        var flipped = new byte[4];
        flipped[0] = bytes[offset + 1];   // 寄存器 1 低字节
        flipped[1] = bytes[offset];       // 寄存器 1 高字节
        flipped[2] = bytes[offset + 3];   // 寄存器 2 低字节
        flipped[3] = bytes[offset + 2];   // 寄存器 2 高字节
        return BitConverter.ToSingle(flipped, 0);
    }

    private static int ReadBigEndianInt32(byte[] bytes, int offset)
    {
        var flipped = new byte[4];
        flipped[0] = bytes[offset + 1];
        flipped[1] = bytes[offset];
        flipped[2] = bytes[offset + 3];
        flipped[3] = bytes[offset + 2];
        return BitConverter.ToInt32(flipped, 0);
    }

    private static long ReadBigEndianInt64(byte[] bytes, int offset)
    {
        var flipped = new byte[8];
        for (var i = 0; i < 4; i++)
        {
            flipped[i * 2] = bytes[offset + i * 2 + 1];
            flipped[i * 2 + 1] = bytes[offset + i * 2];
        }
        return BitConverter.ToInt64(flipped, 0);
    }

    private static double ReadBigEndianDouble(byte[] bytes, int offset)
    {
        var flipped = new byte[8];
        for (var i = 0; i < 4; i++)
        {
            flipped[i * 2] = bytes[offset + i * 2 + 1];
            flipped[i * 2 + 1] = bytes[offset + i * 2];
        }
        return BitConverter.ToDouble(flipped, 0);
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
