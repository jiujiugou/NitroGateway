using HslCommunication;
using HslCommunication.Profinet.Siemens;
using Microsoft.Extensions.Logging;
using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Protocols;
using NitroGateway.Shared;

namespace NitroGateway.Protocols.S7;

/// <summary>
/// Siemens S7 驱动，基于 HslCommunication。地址支持 DB 区（DB1.DBD0）与 M/I/Q 区（M100、I0.0、Q0.2）。
/// <para>
/// <b>并发闸门（ADR-019 P2-1）：</b>单实例内全部通信（读/写/连接/断开/Ping）经 <see cref="_gate"/> 串行化，
/// 防止 1s 采集读 + Webapi 写 + 健康 Ping 并发访问同一个非线程安全 <see cref="SiemensS7Net"/> 客户端导致帧交错/协议失步。
/// </para>
/// <para>
/// <b>失败读不产出伪值（ADR-019 P1-1）：</b>所有读显式检查 Hsl 结果的 IsSuccess，
/// Hsl 失败时 Content 为默认值（float→0），直接取 Content 会把故障读当作 0.0 + Quality Good 写入时序库并上云。
/// </para>
/// </summary>
public sealed class S7Driver : IProtocolDriver, IDisposable
{
    /// <summary>String 点位读取长度（字符）。与 Modbus 的 DefaultStringLength 对齐（协议约定）</summary>
    private const ushort DefaultStringLength = 10;

    private readonly DeviceConnection _connection;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SiemensS7Net? _client;

    public DriverState State { get; private set; } = DriverState.Disconnected;
    public DriverCapability Capability => S7DriverCapability.Instance;

    /// <summary>仅供测试注入已构造客户端（未连接时读操作返回 Failure 而非伪值，ADR-019 P1-1 红绿对照）</summary>
    internal S7Driver(DeviceConnection connection, ILogger logger, SiemensS7Net client) : this(connection, logger)
    {
        _client = client;
    }

    public S7Driver(DeviceConnection connection, ILogger logger)
    {
        _connection = connection;
        _logger = logger;
    }

    public async Task<OperationResult> ConnectAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (State == DriverState.Connected && _client is not null)
                return OperationResult.Success();

            State = DriverState.Connecting;
            ct.ThrowIfCancellationRequested();

            var (ip, port) = EndpointParser.Split(_connection.Endpoint);
            var rack = ToByteParam("Rack", 0);
            var slot = ToByteParam("Slot", 1);
            var cpuType = ParseCpuType(_connection.Parameters.GetValueOrDefault("CpuType")?.ToString());

            var client = new SiemensS7Net(cpuType) { IpAddress = ip, Port = port ?? 102, Rack = rack, Slot = slot };
            try
            {
                // ADR-019 P3-3：连接走异步 API（不再同步 ConnectServer 阻塞），建连后响应取消
                var r = await client.ConnectServerAsync();
                ct.ThrowIfCancellationRequested();

                if (!r.IsSuccess)
                {
                    client.Dispose();
                    State = DriverState.Faulted;
                    return OperationalError.Timeout($"S7 连接失败: {r.Message}");
                }

                _client = client;
                State = DriverState.Connected;
                return OperationResult.Success();
            }
            catch
            {
                // ADR-024 P1-2：建连成功后被取消也走这里——必须关闭已建立的连接，防止 PLC 连接悬挂
                client.Dispose();
                throw;
            }
        }
        catch (OperationCanceledException)
        {
            State = DriverState.Faulted;
            return OperationalError.Timeout("S7 连接已取消");
        }
        catch (Exception ex)
        {
            State = DriverState.Faulted;
            return OperationalError.Timeout($"S7 连接异常: {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<OperationResult> DisconnectAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            try { _client?.ConnectClose(); } catch { }
            _client = null;
            State = DriverState.Disconnected;
            return OperationResult.Success();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<OperationResult> PingAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_client is null) return OperationalError.Unavailable("S7 未连接");
            try
            {
                // ADR-019 P3-2：ping 地址可配置（默认 DB1.DBW0），PLC 无 DB1 时不再恒 ping 失败；
                // ADR-024 P2-2：位地址（DBX/Mx.y）按 Bool 读，否则按 Int16 读
                var address = _connection.Parameters.GetValueOrDefault("PingAddress")?.ToString() ?? "DB1.DBW0";
                HslCommunication.OperateResult r = S7AddressParser.IsBitAddress(address)
                    ? await _client.ReadBoolAsync(address)
                    : await _client.ReadInt16Async(address);
                return r.IsSuccess ? OperationResult.Success() : (OperationResult)OperationalError.Timeout(r.Message);
            }
            catch (Exception ex)
            {
                return OperationalError.Timeout($"Ping 失败: {ex.Message}");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<OperationResult<RawPointValue>> ReadAsync(DevicePoint point, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_client is null)
                return OperationResult<RawPointValue>.Failure(OperationalError.Unavailable("S7 未连接"));

            try
            {
                // ADR-019 P1-1/P2-2：按 DataType 全量映射读方法并显式检查 IsSuccess，
                // 失败抛异常转 OperationResult，驱动层不产出伪值
                var value = await ReadTypedAsync(_client, point.DataType, FormatAddress(point));
                var raw = new RawPointValue { Point = point, Value = value, Timestamp = DateTime.UtcNow };
                return OperationResult<RawPointValue>.Success(raw);
            }
            catch (Exception ex)
            {
                return OperationResult<RawPointValue>.Failure(OperationalError.Protocol($"S7 读取失败: {ex.Message}"));
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<OperationResult<IReadOnlyList<RawPointValue>>> ReadBatchAsync(
        IEnumerable<DevicePoint> points, CancellationToken ct = default)
    {
        var pointList = points.ToList();
        if (pointList.Count == 0) return Array.Empty<RawPointValue>();

        var results = new List<RawPointValue>(pointList.Count);
        foreach (var p in pointList)
        {
            var r = await ReadAsync(p, ct);
            if (r.IsSuccess) results.Add(r.Value!);
        }

        // ADR-019 P3-1：与 Modbus 对齐——全部失败返回 Failure 并复位 Faulted，
        // 避免 S7 设备死掉后 DeviceCollector 报 0/0、熔断器 RecordSuccess、HealthMonitor 不感知故障
        if (results.Count == 0)
        {
            State = DriverState.Faulted;
            return OperationalError.Protocol($"批量读取失败：{pointList.Count} 个点位均未返回数据");
        }

        if (results.Count < pointList.Count)
            _logger.LogWarning("批量读取部分失败：{Ok}/{Total} 个点位成功", results.Count, pointList.Count);

        return results;
    }

    public async Task<OperationResult> WriteAsync(DevicePoint point, object value, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_client is null) return OperationalError.Unavailable("S7 未连接");
            try
            {
                // ADR-019 P2-2：按 DataType 全量映射写方法，不再恒 Convert.ToSingle
                var r = await WriteTypedAsync(_client, point.DataType, FormatAddress(point), value);
                return r.IsSuccess ? OperationResult.Success() : (OperationResult)OperationalError.Protocol(r.Message);
            }
            catch (Exception ex)
            {
                return OperationalError.Protocol($"S7 写入失败: {ex.Message}");
            }
        }
        finally
        {
            _gate.Release();
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

    public void Dispose()
    {
        try { _client?.ConnectClose(); } catch { }
        _client?.Dispose();
        _gate.Dispose();
    }

    /// <summary>
    /// 拼接 Hsl 地址。DB 区沿用地址串自带类型（DBD/DBW/DBB/DBX，含位偏移）；
    /// M/I/Q 区类型由点位 DataType 推导（Bool→位、Byte/String→B、Int16/UInt16→W、其余→D），
    /// 因为非 DB 区地址串通常不带类型后缀（如 M100），Hsl 需要显式类型字符才能按类型读写（ADR-019 P2-3）。
    /// </summary>
    private static string FormatAddress(DevicePoint point) =>
        S7AddressParser.FormatForHsl(point.Address, point.DataType);

    /// <summary>按点位类型读取并检查 Hsl 结果，失败抛 IOException（携带设备侧错误信息）</summary>
    private static async Task<object> ReadTypedAsync(SiemensS7Net client, DataType type, string address) => type switch
    {
        DataType.Bool   => await ReadCheckedAsync(client.ReadBoolAsync(address), "读取 Bool"),
        DataType.Byte   => await ReadCheckedAsync(client.ReadByteAsync(address), "读取 Byte"),
        DataType.Int16  => await ReadCheckedAsync(client.ReadInt16Async(address), "读取 Int16"),
        DataType.UInt16 => await ReadCheckedAsync(client.ReadUInt16Async(address), "读取 UInt16"),
        DataType.Int32  => await ReadCheckedAsync(client.ReadInt32Async(address), "读取 Int32"),
        DataType.UInt32 => await ReadCheckedAsync(client.ReadUInt32Async(address), "读取 UInt32"),
        DataType.Int64  => await ReadCheckedAsync(client.ReadInt64Async(address), "读取 Int64"),
        DataType.UInt64 => await ReadCheckedAsync(client.ReadUInt64Async(address), "读取 UInt64"),
        DataType.Float  => await ReadCheckedAsync(client.ReadFloatAsync(address), "读取 Float"),
        DataType.Double => await ReadCheckedAsync(client.ReadDoubleAsync(address), "读取 Double"),
        DataType.String => await ReadCheckedAsync(client.ReadStringAsync(address, DefaultStringLength), "读取 String"),
        _               => await ReadCheckedAsync(client.ReadFloatAsync(address), "读取 Float")
    };

    /// <summary>解析 CpuType 连接参数。默认 S-1200；未知型号显式报错，不再静默默认（ADR-024 P1-1/P2-1）</summary>
    internal static SiemensPLCS ParseCpuType(string? raw) => raw switch
    {
        null or "" => SiemensPLCS.S1200,
        "S-1500" => SiemensPLCS.S1500,
        "S-300" => SiemensPLCS.S300,
        "S-400" => SiemensPLCS.S400,
        "S-1200" => SiemensPLCS.S1200,
        var other => throw new ArgumentException($"未知的 S7 CpuType: {other}（支持 S-1200/S-1500/S-300/S-400）")
    };

    /// <summary>读取 0-255 整数连接参数；支持数字与字符串（API/CSV 传参），不再 (int) 强转抛 InvalidCastException（ADR-024 P2-1）</summary>
    private byte ToByteParam(string key, byte defaultValue)
    {
        if (!_connection.Parameters.TryGetValue(key, out var raw) || raw is null)
            return defaultValue;
        var value = Convert.ToInt32(raw, System.Globalization.CultureInfo.InvariantCulture);
        if (value is < 0 or > 255)
            throw new ArgumentException($"S7 参数 {key} 需在 0-255 之间: {value}");
        return (byte)value;
    }

    /// <summary>Hsl 读结果转成功值，失败抛 IOException（对齐 Modbus ReadCheckedAsync，避免失败读产出默认值）</summary>
    private static async Task<TValue> ReadCheckedAsync<TValue>(Task<OperateResult<TValue>> task, string what)
    {
        var r = await task;
        return r.IsSuccess ? r.Content : throw new IOException($"{what}失败: {r.Message}");
    }

    /// <summary>按点位类型映射 Hsl 写方法</summary>
    private static Task<OperateResult> WriteTypedAsync(SiemensS7Net client, DataType type, string address, object value) => type switch
    {
        DataType.Bool   => client.WriteAsync(address, Convert.ToBoolean(value)),
        DataType.Byte   => client.WriteAsync(address, Convert.ToByte(value)),
        DataType.Int16  => client.WriteAsync(address, Convert.ToInt16(value)),
        DataType.UInt16 => client.WriteAsync(address, Convert.ToUInt16(value)),
        DataType.Int32  => client.WriteAsync(address, Convert.ToInt32(value)),
        DataType.UInt32 => client.WriteAsync(address, Convert.ToUInt32(value)),
        DataType.Int64  => client.WriteAsync(address, Convert.ToInt64(value)),
        DataType.UInt64 => client.WriteAsync(address, Convert.ToUInt64(value)),
        DataType.Float  => client.WriteAsync(address, Convert.ToSingle(value)),
        DataType.Double => client.WriteAsync(address, Convert.ToDouble(value)),
        DataType.String => client.WriteAsync(address, Convert.ToString(value)),
        _               => client.WriteAsync(address, Convert.ToSingle(value))
    };
}

