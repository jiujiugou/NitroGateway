using HslCommunication.ModBus;
using Microsoft.Extensions.Logging;
using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Protocols;
using NitroGateway.Shared;

namespace NitroGateway.Protocols.Modbus;

/// <summary>
/// Modbus TCP 驱动，基于 HslCommunication ModbusTcpNet。
/// 异步通信；默认 Modbus 标准端口 502 与标准字节序 ABCD（Hsl 默认 5000/CDAB）；
/// 从站地址按协议范围 1-247 收敛；连接失败按超时/通信分类返回。
/// </summary>
public sealed class ModbusTcpDriver : ModbusDriverBase
{
    /// <summary>从站地址范围（Modbus 协议 1-247）</summary>
    private const byte MaxUnitId = 247;

    private readonly DeviceConnection _connection;
    private readonly ModbusTcpNet _client = new();
    private readonly SemaphoreSlim _readLock = new(1, 1);
    private readonly byte _unitId;

    public ModbusTcpDriver(DeviceConnection connection, ILogger logger) : base(logger)
    {
        _connection = connection;
        _unitId = (byte)Math.Clamp(ToInt64(connection.Parameters.GetValueOrDefault("UnitId") ?? 1), 1, MaxUnitId);

        _client.Station = _unitId;
        _client.DataFormat = ParseDataFormat(ToParamString(connection.Parameters.GetValueOrDefault("DataFormat")));
        _client.ConnectTimeOut = connection.ConnectTimeoutMs;
        _client.ReceiveTimeOut = connection.RequestTimeoutMs;
        _client.Port = 502; // Modbus TCP 标准端口；ConnectAsync 可按 Endpoint 覆盖
    }

    protected override SemaphoreSlim ReadGate => _readLock;

    public override async Task<OperationResult> ConnectAsync(CancellationToken ct = default)
    {
        if (State == DriverState.Connected)
            return OperationResult.Success();

        State = DriverState.Connecting;
        ct.ThrowIfCancellationRequested();

        try
        {
            var parts = _connection.Endpoint.Split(':');
            _client.IpAddress = parts[0];
            if (parts.Length > 1 && int.TryParse(parts[1], out var p) && p > 0 && p <= 65535)
                _client.Port = p;

            var result = await _client.ConnectServerAsync();
            if (result.IsSuccess)
            {
                State = DriverState.Connected;
                Logger.LogInformation("Modbus 连接成功: {Endpoint} (UnitId={UnitId}, DataFormat={DataFormat})",
                    _connection.Endpoint, _unitId, _client.DataFormat);
                return OperationResult.Success();
            }

            State = DriverState.Faulted;
            return ClassifyConnectError(result.Message);
        }
        catch (OperationCanceledException)
        {
            State = DriverState.Faulted;
            return OperationalError.Timeout("Modbus 连接已取消");
        }
        catch (Exception ex)
        {
            State = DriverState.Faulted;
            return ClassifyConnectError(ex.Message);
        }
    }

    /// <summary>连接失败分类：超时信息归为 Timeout，其余为 Communication</summary>
    private static OperationResult ClassifyConnectError(string? message)
    {
        var m = message ?? "";
        if (m.Contains("超时", StringComparison.OrdinalIgnoreCase) ||
            m.Contains("timeout", StringComparison.OrdinalIgnoreCase))
        {
            return OperationalError.Timeout($"Modbus 连接超时: {m}");
        }
        return OperationalError.Communication($"Modbus 连接失败: {m}");
    }

    public override Task<OperationResult> DisconnectAsync(CancellationToken ct = default)
    {
        try { _client.ConnectClose(); } catch { }
        State = DriverState.Disconnected;
        return Task.FromResult(OperationResult.Success());
    }

    public override void Dispose() { _client.ConnectClose(); _client.Dispose(); }

    protected override async Task<object[]?> ReadBatchTypedAsync(string address, DataType type, int count)
    {
        var c = (ushort)count;
        return type switch
        {
            DataType.Float   => (await ReadCheckedAsync(_client.ReadFloatAsync(address, c), "读取 Float")).Cast<object>().ToArray(),
            DataType.Int16   => (await ReadCheckedAsync(_client.ReadInt16Async(address, c), "读取 Int16")).Cast<object>().ToArray(),
            DataType.Int32   => (await ReadCheckedAsync(_client.ReadInt32Async(address, c), "读取 Int32")).Cast<object>().ToArray(),
            DataType.UInt16  => (await ReadCheckedAsync(_client.ReadInt16Async(address, c), "读取 UInt16")).Select(v => (object)(ushort)v).ToArray(),
            DataType.UInt32  => (await ReadCheckedAsync(_client.ReadInt32Async(address, c), "读取 UInt32")).Select(v => (object)(uint)v).ToArray(),
            DataType.Int64   => (await ReadCheckedAsync(_client.ReadInt64Async(address, c), "读取 Int64")).Cast<object>().ToArray(),
            DataType.UInt64  => (await ReadCheckedAsync(_client.ReadInt64Async(address, c), "读取 UInt64")).Select(v => (object)(ulong)v).ToArray(),
            DataType.Double  => (await ReadCheckedAsync(_client.ReadDoubleAsync(address, c), "读取 Double")).Cast<object>().ToArray(),
            _ => null    // Bool/String 等不支持批量读的类型，回退逐点
        };
    }

    protected override async Task<object> ReadSingleTypedAsync(DataType type, string address) => type switch
    {
        DataType.Float   => (await ReadCheckedAsync(_client.ReadFloatAsync(address, 1), "读取 Float"))[0],
        DataType.Double  => (await ReadCheckedAsync(_client.ReadDoubleAsync(address, 1), "读取 Double"))[0],
        DataType.Int16   => (await ReadCheckedAsync(_client.ReadInt16Async(address, 1), "读取 Int16"))[0],
        DataType.UInt16  => (ushort)(await ReadCheckedAsync(_client.ReadInt16Async(address, 1), "读取 UInt16"))[0],
        DataType.Int32   => (await ReadCheckedAsync(_client.ReadInt32Async(address, 1), "读取 Int32"))[0],
        DataType.UInt32  => (uint)(await ReadCheckedAsync(_client.ReadInt32Async(address, 1), "读取 UInt32"))[0],
        DataType.Bool    => (await ReadCheckedAsync(_client.ReadBoolAsync(address, 1), "读取 Bool"))[0],
        DataType.Byte    => (byte)(await ReadCheckedAsync(_client.ReadInt16Async(address, 1), "读取 Byte"))[0],
        DataType.Int64   => (await ReadCheckedAsync(_client.ReadInt64Async(address, 1), "读取 Int64"))[0],
        DataType.UInt64  => (ulong)(await ReadCheckedAsync(_client.ReadInt64Async(address, 1), "读取 UInt64"))[0],
        DataType.String  => await ReadCheckedAsync(_client.ReadStringAsync(address, 10), "读取 String"),
        _ => (await ReadCheckedAsync(_client.ReadFloatAsync(address, 1), "读取 Float"))[0]
    };

    protected override async Task<OperationResult> WriteSingleValueAsync(DevicePoint point, string address, object value)
    {
        var result = point.DataType switch
        {
            DataType.Float => await _client.WriteAsync(address, Convert.ToSingle(value)),
            DataType.Int16 => await _client.WriteAsync(address, Convert.ToInt16(value)),
            DataType.Bool  => await _client.WriteAsync(address, Convert.ToBoolean(value)),
            _ => await _client.WriteAsync(address, Convert.ToSingle(value))
        };

        return result.IsSuccess ? OperationResult.Success() : (OperationResult)OperationalError.Protocol(result.Message);
    }
}
