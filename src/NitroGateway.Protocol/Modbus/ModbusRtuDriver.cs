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
            // ADR-003 P3-4：串口超时透传设备连接参数 RequestTimeoutMs
            ReceiveTimeoutMs = connection.RequestTimeoutMs,
            ReadTimeoutMs = connection.RequestTimeoutMs,
            WriteTimeoutMs = connection.RequestTimeoutMs
        };
    }

    /// <summary>读写闸门：连接后为共享串口闸门；未连接时退化为驱动内锁</summary>
    protected override SemaphoreSlim ReadGate => _lease?.Gate ?? _sync;

    /// <summary>持有共享闸门后切换到本驱动的从站号，实现同端口多从站复用</summary>
    protected override void OnGateAcquired()
    {
        if (_lease is not null && _lease.Rtu.Station != _unitId)
            _lease.Rtu.Station = _unitId;
    }

    public override Task<OperationResult> ConnectAsync(CancellationToken ct = default)
    {
        // 已连接且串口句柄健康：直接复用
        if (State == DriverState.Connected && _lease is { } alive && alive.Rtu.IsOpen())
            return Task.FromResult(OperationResult.Success());

        State = DriverState.Connecting;

        try
        {
            // 句柄失效（设备拔出/串口异常）时释放旧租约并重新打开；
            // 串口管理器负责端口共享，同端口多从站仍共用同一句柄
            _lease?.Dispose();
            _lease = _serialPorts.Acquire(_settings);

            State = DriverState.Connected;
            Logger.LogInformation("Modbus RTU 串口就绪: {Port} 从站 {UnitId}",
                _settings.PortName, _unitId);
            return Task.FromResult(OperationResult.Success());
        }
        catch (Exception ex)
        {
            State = DriverState.Faulted;
            _lease?.Dispose();
            _lease = null;
            return Task.FromResult<OperationResult>(OperationalError.Communication($"串口连接失败: {ex.Message}"));
        }
    }

    public override Task<OperationResult> DisconnectAsync(CancellationToken ct = default)
    {
        _lease?.Dispose();
        _lease = null;
        State = DriverState.Disconnected;
        return Task.FromResult(OperationResult.Success());
    }

    public override void Dispose() => DisconnectAsync().GetAwaiter().GetResult();

    /// <summary>共享串口客户端；未连接时抛出</summary>
    private ModbusRtu Rtu => _lease?.Rtu ?? throw new InvalidOperationException("串口未连接");

    protected override async Task<object[]?> ReadBatchTypedAsync(string address, DataType type, int count)
    {
        var c = (ushort)count;
        return type switch
        {
            DataType.Float   => (await ReadCheckedAsync(Rtu.ReadFloatAsync(address, c), "读取 Float")).Cast<object>().ToArray(),
            DataType.Int16   => (await ReadCheckedAsync(Rtu.ReadInt16Async(address, c), "读取 Int16")).Cast<object>().ToArray(),
            DataType.Int32   => (await ReadCheckedAsync(Rtu.ReadInt32Async(address, c), "读取 Int32")).Cast<object>().ToArray(),
            DataType.UInt16  => (await ReadCheckedAsync(Rtu.ReadInt16Async(address, c), "读取 UInt16")).Select(v => (object)(ushort)v).ToArray(),
            DataType.UInt32  => (await ReadCheckedAsync(Rtu.ReadInt32Async(address, c), "读取 UInt32")).Select(v => (object)(uint)v).ToArray(),
            DataType.Int64   => (await ReadCheckedAsync(Rtu.ReadInt64Async(address, c), "读取 Int64")).Cast<object>().ToArray(),
            DataType.UInt64  => (await ReadCheckedAsync(Rtu.ReadInt64Async(address, c), "读取 UInt64")).Select(v => (object)(ulong)v).ToArray(),
            DataType.Double  => (await ReadCheckedAsync(Rtu.ReadDoubleAsync(address, c), "读取 Double")).Cast<object>().ToArray(),
            _ => null    // Bool/String 等不支持批量读的类型，回退逐点
        };
    }

    protected override async Task<object> ReadSingleTypedAsync(DataType type, string address) => type switch
    {
        DataType.Float   => (await ReadCheckedAsync(Rtu.ReadFloatAsync(address, 1), "读取 Float"))[0],
        DataType.Double  => (await ReadCheckedAsync(Rtu.ReadDoubleAsync(address, 1), "读取 Double"))[0],
        DataType.Int16   => (await ReadCheckedAsync(Rtu.ReadInt16Async(address, 1), "读取 Int16"))[0],
        DataType.UInt16  => (ushort)(await ReadCheckedAsync(Rtu.ReadInt16Async(address, 1), "读取 UInt16"))[0],
        DataType.Int32   => (await ReadCheckedAsync(Rtu.ReadInt32Async(address, 1), "读取 Int32"))[0],
        DataType.UInt32  => (uint)(await ReadCheckedAsync(Rtu.ReadInt32Async(address, 1), "读取 UInt32"))[0],
        DataType.Bool    => (await ReadCheckedAsync(Rtu.ReadBoolAsync(address, 1), "读取 Bool"))[0],
        DataType.Byte    => (byte)(await ReadCheckedAsync(Rtu.ReadInt16Async(address, 1), "读取 Byte"))[0],
        DataType.Int64   => (await ReadCheckedAsync(Rtu.ReadInt64Async(address, 1), "读取 Int64"))[0],
        DataType.UInt64  => (ulong)(await ReadCheckedAsync(Rtu.ReadInt64Async(address, 1), "读取 UInt64"))[0],
        DataType.String  => await ReadCheckedAsync(Rtu.ReadStringAsync(address, DefaultStringLength), "读取 String"),
        _ => (await ReadCheckedAsync(Rtu.ReadFloatAsync(address, 1), "读取 Float"))[0]
    };

    protected override async Task<OperationResult> WriteSingleValueAsync(DevicePoint point, string address, object value)
    {
        // ADR-003 P1-2：按 DataType 全量映射 HSL 写方法，不再回退 Convert.ToSingle
        var result = point.DataType switch
        {
            DataType.Bool    => await Rtu.WriteAsync(address, Convert.ToBoolean(value)),
            DataType.Byte    => await Rtu.WriteAsync(address, Convert.ToInt16(value)),  // 1 寄存器，按 short 写入
            DataType.Int16   => await Rtu.WriteAsync(address, Convert.ToInt16(value)),
            DataType.UInt16  => await Rtu.WriteAsync(address, Convert.ToUInt16(value)),
            DataType.Int32   => await Rtu.WriteAsync(address, Convert.ToInt32(value)),
            DataType.UInt32  => await Rtu.WriteAsync(address, Convert.ToUInt32(value)),
            DataType.Int64   => await Rtu.WriteAsync(address, Convert.ToInt64(value)),
            DataType.UInt64  => await Rtu.WriteAsync(address, Convert.ToUInt64(value)),
            DataType.Float   => await Rtu.WriteAsync(address, Convert.ToSingle(value)),
            DataType.Double  => await Rtu.WriteAsync(address, Convert.ToDouble(value)),
            DataType.String  => await Rtu.WriteAsync(address, Convert.ToString(value)),
            _                => await Rtu.WriteAsync(address, Convert.ToSingle(value))
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
