using HslCommunication.Core;
using HslCommunication.ModBus;
using Microsoft.Extensions.Logging;
using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Protocols;
using NitroGateway.Shared;
using System.IO.Ports;

namespace NitroGateway.Protocols.Modbus;

/// <summary>
/// Modbus RTU 串口驱动，基于 HslCommunication ModbusRtu + System.IO.Ports。
/// 串口由 <see cref="ISerialPortManager"/> 统一管理：同一端口多从站共享同一个 ModbusRtu 句柄，
/// 每次通信在持有共享闸门时切换到本驱动的从站号，保证帧级串行。
/// </summary>
public sealed class ModbusRtuDriver : ModbusDriverBase
{
    /// <summary>从站地址范围（Modbus 协议 1-247）</summary>
    private const byte MaxUnitId = 247;

    private readonly ISerialPortManager _serialPorts;
    private readonly SerialPortSettings _settings;
    private readonly byte _unitId;
    private readonly SemaphoreSlim _sync = new(1, 1);

    private SerialPortLease? _lease;

    public ModbusRtuDriver(DeviceConnection connection, ISerialPortManager serialPorts, ILogger logger) : base(logger)
    {
        _serialPorts = serialPorts;
        _unitId = ParseUnitId(connection.Parameters.GetValueOrDefault("UnitId") ?? 1);
        _settings = new SerialPortSettings
        {
            PortName = connection.Endpoint,
            BaudRate = ParseBaudRate(connection.Parameters.GetValueOrDefault("BaudRate") ?? 9600),
            DataBits = (int)ToInt64(connection.Parameters.GetValueOrDefault("DataBits") ?? 8) is var db && db is 7 or 8 ? db : 8,
            Parity = ParseParity(ToParamString(connection.Parameters.GetValueOrDefault("Parity"))),
            StopBits = ParseStopBits(ToParamString(connection.Parameters.GetValueOrDefault("StopBits"))),
            DataFormat = ParseDataFormat(ToParamString(connection.Parameters.GetValueOrDefault("DataFormat"))),
            ReceiveTimeoutMs = connection.RequestTimeoutMs,
            ReadTimeoutMs = connection.RequestTimeoutMs,
            WriteTimeoutMs = connection.RequestTimeoutMs
        };
    }

    /// <summary>读写闸门：连接后为共享串口闸门；未连接时退化为驱动内锁</summary>
    protected override SemaphoreSlim Gate => _lease?.Gate ?? _sync;

    /// <summary>持有共享闸门后切换到本驱动的从站号，实现同端口多从站复用</summary>
    protected override void OnGateAcquired()
    {
        if (_lease is not null && _lease.Rtu.Station != _unitId)
            _lease.Rtu.Station = _unitId;
    }

    public override async Task<OperationResult> ConnectAsync(CancellationToken ct = default)
    {
        // 已连接且串口句柄健康：直接复用
        if (State == DriverState.Connected && _lease is { } alive && alive.Rtu.IsOpen())
            return OperationResult.Success();

        // 再拿当前句柄的共享端口闸门做替换，防止在途读写持有已关闭句柄（帧交错/句柄竞争）
        await _sync.WaitAsync(ct);
        try
        {
            // 二次检查（等待 _sync 期间可能已被其他连接请求完成）
            if (State == DriverState.Connected && _lease is { } healthy && healthy.Rtu.IsOpen())
                return OperationResult.Success();

            State = DriverState.Connecting;
            ct.ThrowIfCancellationRequested();

            try
            {
                // 句柄失效（设备拔出/串口异常）时释放旧租约并重新打开；
                // 串口管理器负责端口共享，同端口多从站仍共用同一句柄
                var gate = _lease?.Gate;
                if (gate is not null)
                    await gate.WaitAsync(ct);
                try
                {
                    _lease?.Dispose();
                    _lease = _serialPorts.Acquire(_settings);
                    State = DriverState.Connected;
                    Logger.LogInformation("Modbus RTU 串口就绪: {Port} 从站 {UnitId}",
                        _settings.PortName, _unitId);
                    return OperationResult.Success();
                }
                finally
                {
                    gate?.Release();
                }
            }
            catch (Exception ex)
            {
                State = DriverState.Faulted;
                // 同上：原子置空后再释放，防止并发 ConnectAsync 刚写入的新租约被延迟的置空覆盖而泄漏。
                var failed = Interlocked.Exchange(ref _lease, null);
                failed?.Dispose();
                return OperationalError.Communication($"串口连接失败: {ex.Message}");
            }
        }
        finally
        {
            _sync.Release();
        }
    }

    public override async Task<OperationResult> DisconnectAsync(CancellationToken ct = default)
    {
        await _sync.WaitAsync(ct);
        try
        {
            var gate = _lease?.Gate;
            if (gate is not null) await gate.WaitAsync(ct);
            try
            {
                // 与 DisposeCore 同款：原子置空后再释放，避免清空动作与并发的 ConnectAsync/DisposeCore 写入互相覆盖。
                var lease = Interlocked.Exchange(ref _lease, null);
                lease?.Dispose();
                State = DriverState.Disconnected;
                return OperationResult.Success();
            }
            finally
            {
                gate?.Release();
            }
        }
        finally
        {
            _sync.Release();
        }
    }

    /// <summary>同步拆除：尽力归还租约，不等 _sync/共享闸门（不排水，ADR-077）。</summary>
    /// <remarks>
    /// 必须用 <c>Interlocked.Exchange(ref _lease, null)</c> 原子地「读旧值 + 置空」，<b>不能</b>写成
    /// <c>_lease?.Dispose(); _lease = null;</c>。
    /// <para>
    /// 原因：<see cref="DisposeCore"/> 按设计<b>不取 _sync</b>（同步释放不排水），因此可与
    /// <see cref="ConnectAsync"/> 真正并发；而 <c>_lease.Dispose()</c> 内部会经
    /// <c>SerialPortManager.Release</c> 走 <c>lock (_lock)</c>——这是一个可阻塞、可被调度切走的点。
    /// 危险交错（设初始 <c>_lease = L1</c>）：
    /// </para>
    /// <list type="number">
    /// <item>本方法读到 <c>_lease</c> = L1 并调用 <c>L1.Dispose()</c>，卡在 <c>lock (_lock)</c> 上（尚未执行置空）；</item>
    /// <item>此时字段 <c>_lease</c> 仍是 L1，并发 <see cref="ConnectAsync"/> 执行
    ///       <c>_lease = Acquire(_settings)</c>，写入新租约 L2；</item>
    /// <item>本方法从锁返回后继续执行 <c>_lease = null</c>，把刚写入的 L2 <b>覆盖丢失</b>；</item>
    /// <item>结果：管理器里 L2 的引用计数仍为 1（串口保持打开、条目仍在 <c>_ports</c>），
    ///       但驱动再也不持有 L2、无人释放它 → <b>串口租约泄漏、端口永不关闭</b>（静默、窗口窄、难复现）。
    ///       收尾的 <see cref="DisconnectAsync"/> 读到 <c>_lease == null</c> 也救不回来。</item>
    /// </list>
    /// <para>
    /// 原子交换后，「清空」成为不可分割的一步：并发 <c>ConnectAsync</c> 的写入只可能发生在交换<b>之前</b>
    /// （Exchange 返回的就是那个新值，会被正常释放）或交换<b>之后</b>（留在字段里，由后续 Disconnect 释放），
    /// 不再存在「延迟执行的 <c>= null</c> 覆盖新值」的窗口。Coyote 用例
    /// <c>ModbusRtuDriverInvariants.I3_LeaseAccounted_*</c> 守此性质。
    /// </para>
    /// </remarks>
    protected override void DisposeCore()
    {
        var lease = Interlocked.Exchange(ref _lease, null);
        lease?.Dispose();
        State = DriverState.Disconnected;
    }

    /// <summary>异步拆除：走 <see cref="DisconnectAsync"/> 优雅断开（等 _sync + 共享闸门，不阻塞线程）。</summary>
    protected override async ValueTask DisposeAsyncCore()
    {
        await DisconnectAsync();
    }

    /// <summary>共享串口客户端；未连接时抛出</summary>
    private ModbusRtu Rtu => _lease?.Rtu ?? throw new InvalidOperationException("串口未连接");

    protected override async Task<object[]?> ReadBatchTypedAsync(string address, DataType type, int count)
    {
        var c = (ushort)count;
        return type switch
        {
            DataType.Float => (await ReadCheckedAsync(Rtu.ReadFloatAsync(address, c), "读取 Float")).Cast<object>().ToArray(),
            DataType.Int16 => (await ReadCheckedAsync(Rtu.ReadInt16Async(address, c), "读取 Int16")).Cast<object>().ToArray(),
            DataType.Int32 => (await ReadCheckedAsync(Rtu.ReadInt32Async(address, c), "读取 Int32")).Cast<object>().ToArray(),
            DataType.UInt16 => (await ReadCheckedAsync(Rtu.ReadInt16Async(address, c), "读取 UInt16")).Select(v => (object)(ushort)v).ToArray(),
            DataType.UInt32 => (await ReadCheckedAsync(Rtu.ReadInt32Async(address, c), "读取 UInt32")).Select(v => (object)(uint)v).ToArray(),
            DataType.Int64 => (await ReadCheckedAsync(Rtu.ReadInt64Async(address, c), "读取 Int64")).Cast<object>().ToArray(),
            DataType.UInt64 => (await ReadCheckedAsync(Rtu.ReadInt64Async(address, c), "读取 UInt64")).Select(v => (object)(ulong)v).ToArray(),
            DataType.Double => (await ReadCheckedAsync(Rtu.ReadDoubleAsync(address, c), "读取 Double")).Cast<object>().ToArray(),
            _ => null    // Bool/String 等不支持批量读的类型，回退逐点
        };
    }

    protected override async Task<object> ReadSingleTypedAsync(DataType type, string address) => type switch
    {
        DataType.Float => (await ReadCheckedAsync(Rtu.ReadFloatAsync(address, 1), "读取 Float"))[0],
        DataType.Double => (await ReadCheckedAsync(Rtu.ReadDoubleAsync(address, 1), "读取 Double"))[0],
        DataType.Int16 => (await ReadCheckedAsync(Rtu.ReadInt16Async(address, 1), "读取 Int16"))[0],
        DataType.UInt16 => (ushort)(await ReadCheckedAsync(Rtu.ReadInt16Async(address, 1), "读取 UInt16"))[0],
        DataType.Int32 => (await ReadCheckedAsync(Rtu.ReadInt32Async(address, 1), "读取 Int32"))[0],
        DataType.UInt32 => (uint)(await ReadCheckedAsync(Rtu.ReadInt32Async(address, 1), "读取 UInt32"))[0],
        DataType.Bool => (await ReadCheckedAsync(Rtu.ReadBoolAsync(address, 1), "读取 Bool"))[0],
        DataType.Byte => (byte)(await ReadCheckedAsync(Rtu.ReadInt16Async(address, 1), "读取 Byte"))[0],
        DataType.Int64 => (await ReadCheckedAsync(Rtu.ReadInt64Async(address, 1), "读取 Int64"))[0],
        DataType.UInt64 => (ulong)(await ReadCheckedAsync(Rtu.ReadInt64Async(address, 1), "读取 UInt64"))[0],
        DataType.String => await ReadCheckedAsync(Rtu.ReadStringAsync(address, DefaultStringLength), "读取 String"),
        _ => (await ReadCheckedAsync(Rtu.ReadFloatAsync(address, 1), "读取 Float"))[0]
    };

    protected override async Task<OperationResult> WriteSingleValueAsync(DevicePoint point, string address, object value)
    {
        var result = point.DataType switch
        {
            DataType.Bool => await Rtu.WriteAsync(address, Convert.ToBoolean(value, System.Globalization.CultureInfo.InvariantCulture)),
            DataType.Byte => await Rtu.WriteAsync(address, Convert.ToInt16(value, System.Globalization.CultureInfo.InvariantCulture)),  // 1 寄存器，按 short 写入
            DataType.Int16 => await Rtu.WriteAsync(address, Convert.ToInt16(value, System.Globalization.CultureInfo.InvariantCulture)),
            DataType.UInt16 => await Rtu.WriteAsync(address, Convert.ToUInt16(value, System.Globalization.CultureInfo.InvariantCulture)),
            DataType.Int32 => await Rtu.WriteAsync(address, Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture)),
            DataType.UInt32 => await Rtu.WriteAsync(address, Convert.ToUInt32(value, System.Globalization.CultureInfo.InvariantCulture)),
            DataType.Int64 => await Rtu.WriteAsync(address, Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture)),
            DataType.UInt64 => await Rtu.WriteAsync(address, Convert.ToUInt64(value, System.Globalization.CultureInfo.InvariantCulture)),
            DataType.Float => await Rtu.WriteAsync(address, Convert.ToSingle(value, System.Globalization.CultureInfo.InvariantCulture)),
            DataType.Double => await Rtu.WriteAsync(address, Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture)),
            DataType.String => await Rtu.WriteAsync(address, Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)),
            _ => await Rtu.WriteAsync(address, Convert.ToSingle(value, System.Globalization.CultureInfo.InvariantCulture))
        };

        return result.IsSuccess ? OperationResult.Success() : (OperationResult)OperationalError.Protocol(result.Message);
    }

    private static byte ParseUnitId(object raw) =>
        (byte)Math.Clamp(ToInt64(raw), 1, MaxUnitId);

    private static int ParseBaudRate(object raw)
    {
        var baud = (int)Math.Clamp(ToInt64(raw), 1200, 115200);
        return baud;
    }

    private static Parity ParseParity(string? raw) => raw?.ToUpperInvariant() switch
    {
        "EVEN" => Parity.Even,
        "ODD" => Parity.Odd,
        "MARK" => Parity.Mark,
        "SPACE" => Parity.Space,
        _ => Parity.None
    };

    private static StopBits ParseStopBits(string? raw) => raw?.ToUpperInvariant() switch
    {
        "TWO" or "2" => StopBits.Two,
        _ => StopBits.One
    };
}
