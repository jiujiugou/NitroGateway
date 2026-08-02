using HslCommunication;
using HslCommunication.Core;
using Microsoft.Extensions.Logging;
using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Protocols;
using NitroGateway.Shared;
using System.Text.Json;

namespace NitroGateway.Protocols.Modbus;

/// <summary>
/// Modbus 驱动公共基类：地址解析、单点读写、批量合并读。
/// TCP / RTU 的差异（客户端、读写闸门、站号切换）通过抽象成员注入。
/// 通信一律走异步 API，避免同步阻塞线程池；批量读全部点位失败时复位状态，
/// 让上层重试管线能够重新建连。
/// </summary>
public abstract class ModbusDriverBase : IProtocolDriver
{
    /// <summary>合并 Range 时允许的最大间隔（寄存器数）。≤此值则合并为一次读取</summary>
    private const int MaxMergeGap = 2;

    /// <summary>Modbus 单次最大寄存器数（协议限制，功能码 03/04 上限为 125）</summary>
    private const int MaxRegistersPerRequest = 125;

    protected readonly ModbusAddressParser AddressParser = new();
    protected readonly ILogger Logger;

    protected ModbusDriverBase(ILogger logger) => Logger = logger;

    public DriverState State { get; protected set; } = DriverState.Disconnected;
    public DriverCapability Capability => ModbusDriverCapability.Instance;

    /// <summary>读写闸门：TCP 为驱动内锁，RTU 为同端口共享闸门</summary>
    protected abstract SemaphoreSlim ReadGate { get; }

    /// <summary>获取到读写闸门后回调；RTU 驱动在此切换到本驱动的从站号</summary>
    protected virtual void OnGateAcquired() { }

    /// <summary>同类批量读（按 DataType 分组后整组读取）。不支持批量读的类型返回 null（回退逐点）。</summary>
    protected abstract Task<object[]?> ReadBatchTypedAsync(string address, DataType type, int count);

    /// <summary>单点读，返回与 DataType 对应的值对象；失败抛异常（由调用方转为 OperationResult）</summary>
    protected abstract Task<object> ReadSingleTypedAsync(DataType type, string address);

    /// <summary>单点写，返回操作结果</summary>
    protected abstract Task<OperationResult> WriteSingleValueAsync(DevicePoint point, string address, object value);

    public abstract Task<OperationResult> ConnectAsync(CancellationToken ct = default);
    public abstract Task<OperationResult> DisconnectAsync(CancellationToken ct = default);
    public abstract void Dispose();

    // ─────────────── 公共参数解析（兼容 System.Text.Json 的 JsonElement） ───────────────

    /// <summary>HSL 读取结果转成功值，失败抛 IOException（携带设备侧错误信息）</summary>
    protected static async Task<TValue> ReadCheckedAsync<TValue>(Task<OperateResult<TValue>> task, string what)
    {
        var r = await task;
        return r.IsSuccess ? r.Content : throw new IOException($"{what}失败: {r.Message}");
    }

    /// <summary>兼容 System.Text.Json 反序列化后的 JsonElement 数值</summary>
    protected static long ToInt64(object raw) => raw switch
    {
        JsonElement je when je.ValueKind == JsonValueKind.Number => je.GetInt64(),
        JsonElement je when long.TryParse(je.GetString(), out var v) => v,
        _ => Convert.ToInt64(raw)
    };

    /// <summary>兼容 System.Text.Json 反序列化后的 JsonElement 字符串</summary>
    protected static string? ToParamString(object? raw) => raw switch
    {
        null => null,
        JsonElement je when je.ValueKind == JsonValueKind.String => je.GetString(),
        JsonElement je => je.ToString(),
        _ => raw.ToString()
    };

    /// <summary>
    /// 解析寄存器字节序。Modbus 标准约定为 ABCD（高字在前），
    /// HslCommunication 默认 CDAB，因此未配置时显式采用 ABCD。
    /// </summary>
    protected static DataFormat ParseDataFormat(string? raw) => raw?.ToUpperInvariant() switch
    {
        "CDAB" => DataFormat.CDAB,
        "BADC" => DataFormat.BADC,
        "DCBA" => DataFormat.DCBA,
        _ => DataFormat.ABCD
    };

    // ───────────────────────── 单点读 / 写 / Ping ─────────────────────────

    public virtual async Task<OperationResult> PingAsync(CancellationToken ct = default)
    {
        await ReadGate.WaitAsync(ct);
        try
        {
            OnGateAcquired();
            if (State != DriverState.Connected)
                return OperationalError.Unavailable("Modbus 未连接");

            try
            {
                await ReadSingleTypedAsync(DataType.Int16, "0");
                return OperationResult.Success();
            }
            catch (Exception ex)
            {
                return OperationalError.Timeout($"Ping 失败: {ex.Message}");
            }
        }
        finally
        {
            ReadGate.Release();
        }
    }

    public async Task<OperationResult<RawPointValue>> ReadAsync(DevicePoint point, CancellationToken ct = default)
    {
        await ReadGate.WaitAsync(ct);
        try
        {
            OnGateAcquired();
            var addr = AddressParser.ParseWithCount(point.Address, point.DataType);
            return await TryReadSingleAsync(point, addr, ct);
        }
        finally
        {
            ReadGate.Release();
        }
    }

    public async Task<OperationResult> WriteAsync(DevicePoint point, object value, CancellationToken ct = default)
    {
        await ReadGate.WaitAsync(ct);
        try
        {
            OnGateAcquired();
            if (State != DriverState.Connected)
                return OperationalError.Unavailable("Modbus 未连接");

            var addr = AddressParser.ParseWithCount(point.Address, point.DataType);

            try
            {
                return await WriteSingleValueAsync(point, ToHslAddress(addr), value);
            }
            catch (Exception ex)
            {
                return OperationalError.Protocol($"写入失败: {ex.Message}");
            }
        }
        finally
        {
            ReadGate.Release();
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

    // ───────────────────────── 批量读取 ─────────────────────────

    /// <summary>
    /// 批量读取。策略：
    /// 1. 按功能区(Area)分组
    /// 2. 组内按地址排序
    /// 3. 连续地址（间隔 ≤ MaxMergeGap 寄存器）合并为一个 Range
    /// 4. 每个 Range 发一次 Modbus 多寄存器读指令
    /// 5. 从返回的字节流中按偏移量拆解出每个点位的值
    /// </summary>
    public async Task<OperationResult<IReadOnlyList<RawPointValue>>> ReadBatchAsync(
        IEnumerable<DevicePoint> points, CancellationToken ct = default)
    {
        await ReadGate.WaitAsync(ct);
        try
        {
            OnGateAcquired();
            if (State != DriverState.Connected)
                return OperationalError.Unavailable("Modbus 未连接");

            var pointList = points.ToList();
            if (pointList.Count == 0)
                return Array.Empty<RawPointValue>();

            // 1. 解析地址 + 寄存器数量
            var parsed = pointList.Select(p => new ParsedPoint(
                p, AddressParser.ParseWithCount(p.Address, p.DataType))).ToList();

            // 2. 合并成 Range
            var ranges = MergeRanges(parsed);

            // 3. 逐个 Range 批量读取
            var results = new List<RawPointValue>();
            foreach (var range in ranges)
            {
                var rangeResults = await ReadRangeAsync(range, ct);
                results.AddRange(rangeResults);
            }

            // 4. 全部点位均未取到值 → 通信级故障：复位状态，让重试管线重新建连
            if (results.Count == 0)
            {
                State = DriverState.Faulted;
                return OperationalError.Protocol($"批量读取失败：{pointList.Count} 个点位均未返回数据");
            }

            return results;
        }
        catch (Exception ex)
        {
            State = DriverState.Faulted;
            return OperationalError.Protocol($"批量读取失败: {ex.Message}");
        }
        finally
        {
            ReadGate.Release();
        }
    }

    // ───────────────────────── 内部类型 ─────────────────────────

    private sealed record ParsedPoint(DevicePoint Point, ModbusAddress Addr);

    private sealed record ReadRange(
        ModbusArea Area,
        List<ParsedPoint> Points,
        ushort StartOffset,
        ushort TotalRegisters);

    // ───────────────────────── 单点读取 ─────────────────────────

    private async Task<OperationResult<RawPointValue>> TryReadSingleAsync(
        DevicePoint point, ModbusAddress addr, CancellationToken ct)
    {
        var address = ToHslAddress(addr);
        try
        {
            var value = await ReadSingleTypedAsync(point.DataType, address);
            return OperationResult<RawPointValue>.Success(
                new RawPointValue { Point = point, Value = value, Timestamp = DateTime.UtcNow });
        }
        catch (Exception ex)
        {
            return OperationResult<RawPointValue>.Failure(
                OperationalError.Protocol($"读取失败: {ex.Message}"));
        }
    }

    // ───────────────────────── Range 合并 ─────────────────────────

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

    // ───────────────────────── 批量 Range 读取 ─────────────────────────

    /// <summary>
    /// Range 内按 DataType 分组，调 HSL 同类批量读方法。
    /// 同类型一次批量读，异类回退逐点。不自己解析字节。
    /// </summary>
    private async Task<List<RawPointValue>> ReadRangeAsync(ReadRange range, CancellationToken ct)
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
                        var s = await TryReadSingleAsync(pp.Point, pp.Addr, ct);
                        if (s.IsSuccess) results.Add(s.Value!);
                    }
                    continue;
                }

                var hslAddr = ToHslAddress(range.Area, pts[0].Addr.Offset);
                var values = await ReadBatchTypedAsync(hslAddr, typeGroup.Key, pts.Count);

                if (values is not null)
                {
                    for (var i = 0; i < pts.Count && i < values.Length; i++)
                        results.Add(new RawPointValue { Point = pts[i].Point, Value = values[i], Timestamp = DateTime.UtcNow });
                }
                else
                {
                    foreach (var pp in pts)
                    {
                        var s = await TryReadSingleAsync(pp.Point, pp.Addr, ct);
                        if (s.IsSuccess) results.Add(s.Value!);
                    }
                }
            }
            catch
            {
                foreach (var pp in typeGroup)
                {
                    var s = await TryReadSingleAsync(pp.Point, pp.Addr, ct);
                    if (s.IsSuccess) results.Add(s.Value!);
                }
            }
        }

        return results;
    }

    // ───────────────────────── 地址转换 ─────────────────────────

    /// <summary>ModbusAddress → HSL 地址字符串</summary>
    protected static string ToHslAddress(ModbusAddress a) => ToHslAddress(a.Area, a.Offset);

    /// <summary>Area + Offset → HSL 地址字符串</summary>
    protected static string ToHslAddress(ModbusArea area, ushort offset) => area switch
    {
        ModbusArea.InputRegister => $"x=4;{offset}",
        ModbusArea.Coil => $"x=1;{offset}",
        ModbusArea.DiscreteInput => $"x=2;{offset}",
        _ => offset.ToString()    // HoldingRegister: 直接用数字
    };
}
