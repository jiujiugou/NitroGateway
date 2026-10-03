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

    protected const int DefaultStringLength = 10;

    protected readonly ModbusAddressParser AddressParser = new();
    protected readonly ILogger Logger;

    protected ModbusDriverBase(ILogger logger) => Logger = logger;

    public DriverState State { get; protected set; } = DriverState.Disconnected;
    public DriverCapability Capability => ModbusDriverCapability.Instance;

    /// <summary>读写闸门：TCP 为驱动内锁，RTU 为同端口共享闸门</summary>
    protected abstract SemaphoreSlim Gate { get; }

    /// <summary>获取到读写闸门后回调；RTU 驱动在此切换到本驱动的从站号</summary>
    protected virtual void OnGateAcquired() { }

    /// <summary>同类批量读（按 DataType 分组后整组读取）。不支持批量读的类型返回 null（回退逐点）。</summary>
    protected abstract Task<object[]?> ReadBatchTypedAsync(string address, DataType type, int count);

    /// <summary>单点读，返回与 DataType 对应的值对象；失败抛异常（由调用方转为 OperationResult）</summary>
    protected abstract Task<object> ReadSingleTypedAsync(DataType type, string address);

    /// <summary>单点写，返回操作结果</summary>
    protected abstract Task<OperationResult> WriteSingleValueAsync(DevicePoint point, string address, object value);

    /// <summary>
    /// 在 <see cref="Gate"/> 保护下执行一段操作。
    /// <para><b>并发所有权（ADR-074）：</b>同一驱动实例对底层客户端的所有访问——读/写/Ping，
    /// 以及 TCP 驱动的建连/断开——都必须经<b>同一把</b> <see cref="Gate"/> 串行。
    /// 规则在此一处定义：新增方法只要走 <c>GuardedAsync</c>（或自行取同一闸门）就不会漏加锁。</para>
    /// </summary>
    protected async Task<T> GuardedAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try { return await action(ct); }
        finally { Gate.Release(); }
    }

    public abstract Task<OperationResult> ConnectAsync(CancellationToken ct = default);
    public abstract Task<OperationResult> DisconnectAsync(CancellationToken ct = default);

    /// <summary>0=未释放，1=已释放；同步/异步释放共用一个幂等位。</summary>
    private int _disposed;

    /// <summary>是否已释放。</summary>
    protected bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>
    /// 同步释放：尽力而为、<b>不排水</b>（ADR-077）——不等待闸门/在途操作；幂等、不抛。
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { DisposeCore(); } catch { /* Dispose 不向外抛 */ }
    }

    /// <summary>
    /// 异步释放：优雅拆除——在本驱动闸门保护下关闭（可等待在途操作完成，但不阻塞线程）；幂等、不抛。
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { await DisposeAsyncCore(); } catch { /* Dispose 不向外抛 */ }
    }

    /// <summary>同步拆除核心：不得等待闸门/在途操作（ADR-077）。</summary>
    protected abstract void DisposeCore();

    /// <summary>异步拆除核心：默认退化为同步拆除；子类可覆写为「取本驱动闸门后拆除」。</summary>
    protected virtual ValueTask DisposeAsyncCore()
    {
        DisposeCore();
        return ValueTask.CompletedTask;
    }

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
        _ => Convert.ToInt64(raw, System.Globalization.CultureInfo.InvariantCulture)
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

    // ───────────────────────── 单点读 / 写 / Ping ─────────────
    /// <summary>Ping 失败信息转用户可读文案：超时/断连归"从站无响应"，其余归"从站异常"。</summary>
    private static string ClassifyPingError(string? raw)
    {
        var m = raw ?? "";
        if (m.Contains("超时", StringComparison.OrdinalIgnoreCase) ||
            m.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
            m.Contains("socket exception", StringComparison.OrdinalIgnoreCase))
        {
            return "从站无响应（接收数据超时）：请检查从站地址/UnitId、网络与防火墙";
        }
        return "从站无响应：请检查从站地址/UnitId 与设备状态";
    }

    public virtual async Task<OperationResult> PingAsync(CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
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
                // 转成可操作文案：超时/断连归"无响应"，其余归"从站异常"，不透传内部细节。
                return OperationalError.Timeout(ClassifyPingError(ex.Message));
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task<OperationResult<RawPointValue>> ReadAsync(DevicePoint point, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            OnGateAcquired();
            var addr = AddressParser.ParseWithCount(point.Address, point.DataType);
            return await TryReadSingleAsync(point, addr, ct);
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task<OperationResult> WriteAsync(DevicePoint point, object value, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
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
            Gate.Release();
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
        await Gate.WaitAsync(ct);
        try
        {
            OnGateAcquired();
            if (State != DriverState.Connected)
                return OperationalError.Unavailable("Modbus 未连接");

            var pointList = points.ToList();
            if (pointList.Count == 0)
            {
                // 否则断开后 State 仍为 Connected 且无数据流量，设备永远假在线
                try
                {
                    await ReadSingleTypedAsync(DataType.Int16, "0");
                    return Array.Empty<RawPointValue>();
                }
                catch (Exception ex)
                {
                    State = DriverState.Faulted;
                    return OperationalError.Protocol($"链路探测失败: {ex.Message}");
                }
            }

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
            //    失败即重建连接是正确防护；重连频率由上层熔断器（连续 3 次失败 Trip）兜底，
            //    健康设备长连接不受影响（仅故障设备每轮重连，且连接日志为 Debug）。
            if (results.Count == 0)
            {
                State = DriverState.Faulted;
                return OperationalError.Protocol($"批量读取失败：{pointList.Count} 个点位均未返回数据");
            }

            if (results.Count < pointList.Count)
                Logger.LogWarning("批量读取部分失败：{Ok}/{Total} 个点位成功", results.Count, pointList.Count);

            return results;
        }
        catch (Exception ex)
        {
            State = DriverState.Faulted;
            return OperationalError.Protocol($"批量读取失败: {ex.Message}");
        }
        finally
        {
            Gate.Release();
        }
    }

    // ───────────────────────── 内部类型 ─────────────────────────

    private sealed record ParsedPoint(DevicePoint Point, ModbusAddress Addr);

    private sealed record ReadRange(
        ModbusArea Area,
        List<ParsedPoint> Points);

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
                    currentEnd = point.Addr.Offset + point.Addr.Count;
                }
            }
            FlushRange();

            void FlushRange()
            {
                ranges.Add(new ReadRange(areaGroup.Key, currentPoints));
            }
        }

        return ranges;
    }

    // ───────────────────────── 批量 Range 读取 ─────────────────────────

    private async Task<List<RawPointValue>> ReadRangeAsync(ReadRange range, CancellationToken ct)
    {
        var results = new List<RawPointValue>();

        foreach (var typeGroup in range.Points.GroupBy(p => p.Point.DataType))
        {
            var regsPerPoint = ModbusAddressParser.GetRegisterCount(typeGroup.Key);
            var pts = typeGroup.OrderBy(p => p.Addr.Offset).ToList();
            var segments = ModbusBatchPlanner.SplitContiguousSegments(
                pts.Select(p => ((int)p.Addr.Offset, (int)regsPerPoint)).ToList());

            foreach (var segmentIndices in segments)
            {
                var segment = segmentIndices.Select(i => pts[i]).ToList();
                var totalRegs = segment.Count * regsPerPoint;

                if (totalRegs > MaxRegistersPerRequest)
                {
                    await ReadSegmentFallbackAsync(segment, ct, results);
                    continue;
                }

                try
                {
                    var hslAddr = ToHslAddress(range.Area, segment[0].Addr.Offset);
                    var values = await ReadBatchTypedAsync(hslAddr, typeGroup.Key, segment.Count);

                    if (values is not null)
                    {
                        for (var i = 0; i < segment.Count && i < values.Length; i++)
                            results.Add(new RawPointValue { Point = segment[i].Point, Value = values[i], Timestamp = DateTime.UtcNow });
                    }
                    else
                    {
                        await ReadSegmentFallbackAsync(segment, ct, results);
                    }
                }
                catch
                {
                    await ReadSegmentFallbackAsync(segment, ct, results);
                }
            }
        }

        return results;
    }

    /// <summary>段内逐点读取（批量读失败/超限/不支持时的回退路径）</summary>
    private async Task ReadSegmentFallbackAsync(List<ParsedPoint> segment, CancellationToken ct, List<RawPointValue> results)
    {
        foreach (var pp in segment)
        {
            var s = await TryReadSingleAsync(pp.Point, pp.Addr, ct);
            if (s.IsSuccess) results.Add(s.Value!);
        }
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
        _ => offset.ToString(System.Globalization.CultureInfo.InvariantCulture)    // HoldingRegister: 直接用数字
    };
}
