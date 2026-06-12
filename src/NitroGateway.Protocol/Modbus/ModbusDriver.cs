using FluentModbus;
using Microsoft.Extensions.Logging;
using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Protocols;
using NitroGateway.Shared;

namespace NitroGateway.Protocol.Modbus;

/// <summary>
/// Modbus 协议驱动，基于 FluentModbus 实现。
/// </summary>
public sealed class ModbusDriver : IProtocolDriver, IAsyncDisposable
{
    private readonly DeviceConnection _connection;
    private readonly ModbusEndian _endian;
    private readonly ILogger<ModbusDriver> _logger;
    private readonly ModbusAddressParser _addressParser = new();
    private readonly ModbusTcpClient _client = new();
    private byte _unitId = 1;

    /// <inheritdoc />
    public DriverState State { get; private set; } = DriverState.Disconnected;

    /// <inheritdoc />
    public DriverCapability Capability => ModbusDriverCapability.Instance;

    public ModbusDriver(DeviceConnection connection, ILogger<ModbusDriver> logger)
    {
        _connection = connection;
        _logger = logger;
        _endian = ParseEndian(connection.Parameters.GetValueOrDefault("Endian"));
        _unitId = (byte)(int)(connection.Parameters.GetValueOrDefault("UnitId") ?? 1);

        _client.ConnectTimeout = connection.ConnectTimeoutMs;
        _client.ReadTimeout = connection.RequestTimeoutMs;
        _client.WriteTimeout = connection.RequestTimeoutMs;
    }

    /// <inheritdoc />
    public Task<OperationResult> ConnectAsync(CancellationToken ct = default)
    {
        if (State == DriverState.Connected)
            return Task.FromResult(OperationResult.Success());

        State = DriverState.Connecting;

        try
        {
            // 解析 Endpoint: "192.168.1.100:502"
            var parts = _connection.Endpoint.Split(':');
            var host = parts[0];
            var port = parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : 502;

            _client.Connect(host, _endian == ModbusEndian.DCBA
                ? FluentModbus.ModbusEndianness.LittleEndian
                : FluentModbus.ModbusEndianness.BigEndian);

            State = DriverState.Connected;
            _logger.LogInformation("Modbus 连接成功: {Endpoint}", _connection.Endpoint);
            return Task.FromResult(OperationResult.Success());
        }
        catch (Exception ex)
        {
            State = DriverState.Faulted;
            _logger.LogError(ex, "Modbus 连接失败: {Endpoint}", _connection.Endpoint);
            return Task.FromResult<OperationResult>(
                OperationalError.Timeout($"Modbus 连接失败: {ex.Message}"));
        }
    }

    /// <inheritdoc />
    public Task<OperationResult> DisconnectAsync(CancellationToken ct = default)
    {
        try
        {
            _client.Disconnect();
            State = DriverState.Disconnected;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Modbus 断开异常");
        }
        return Task.FromResult(OperationResult.Success());
    }

    /// <inheritdoc />
    public Task<OperationResult> PingAsync(CancellationToken ct = default)
    {
        if (!_client.IsConnected || State != DriverState.Connected)
            return Task.FromResult<OperationResult>(
                OperationalError.Unavailable("Modbus 未连接"));

        try
        {
            _client.ReadHoldingRegisters(_unitId, 0, 1);
            return Task.FromResult(OperationResult.Success());
        }
        catch (Exception ex)
        {
            return Task.FromResult<OperationResult>(
                OperationalError.Timeout($"Modbus Ping 失败: {ex.Message}"));
        }
    }

    /// <inheritdoc />
    public async Task<OperationResult<RawPointValue>> ReadAsync(DevicePoint point, CancellationToken ct = default)
    {
        var result = await ReadBatchAsync([point], ct);
        if (result.IsFailure) return result.Error!;
        var first = result.Value!.FirstOrDefault();
        return first is not null
            ? OperationResult<RawPointValue>.Success(first)
            : OperationalError.Protocol($"读取点位失败: {point.Name}");
    }

    /// <inheritdoc />
    public async Task<OperationResult<IReadOnlyList<RawPointValue>>> ReadBatchAsync(
        IEnumerable<DevicePoint> points, CancellationToken ct = default)
    {
        if (!_client.IsConnected || State != DriverState.Connected)
            return OperationalError.Unavailable("Modbus 未连接");

        var pointList = points.ToList();
        if (pointList.Count == 0) return Array.Empty<RawPointValue>();

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
                    // 线圈/离散输入：每个点位 1 bit
                    var coilBytes = firstAddr.Area == ModbusArea.Coil
                        ? _client.ReadCoils(_unitId, firstAddr.Offset, totalCount)
                        : _client.ReadDiscreteInputs(_unitId, firstAddr.Offset, totalCount);

                    for (var i = 0; i < groupList.Count && i < coilBytes.Length; i++)
                    {
                        results.Add(new RawPointValue
                        {
                            Point = groupList[i],
                            RawData = new[] { (ushort)(coilBytes[i] != 0 ? 1 : 0) },
                            Timestamp = timestamp
                        });
                    }
                }
                else
                {
                    // 寄存器
                    Span<byte> rawBytes = firstAddr.Area switch
                    {
                        ModbusArea.HoldingRegister =>
                            _client.ReadHoldingRegisters(_unitId, firstAddr.Offset, totalCount),
                        ModbusArea.InputRegister =>
                            _client.ReadInputRegisters(_unitId, firstAddr.Offset, totalCount),
                        _ => throw new NotSupportedException($"不支持的功能区: {firstAddr.Area}")
                    };

                    var byteOffset = 0;
                    foreach (var point in groupList)
                    {
                        var addr = _addressParser.ParseWithCount(point.Address, point.DataType);
                        var byteCount = addr.Count * 2;
                        var segment = new ushort[addr.Count];

                        for (var i = 0; i < addr.Count && byteOffset + (i * 2) + 1 < rawBytes.Length; i++)
                            segment[i] = (ushort)((rawBytes[byteOffset + i * 2] << 8) | rawBytes[byteOffset + i * 2 + 1]);

                        byteOffset += byteCount;

                        results.Add(new RawPointValue
                        {
                            Point = point,
                            RawData = segment,
                            Timestamp = timestamp
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Modbus 批量读失败: Area={Area} Offset={Offset} Count={Count}",
                    firstAddr.Area, firstAddr.Offset, totalCount);
            }
        }

        return results;
    }

    /// <inheritdoc />
    public async Task<OperationResult> WriteAsync(DevicePoint point, object value, CancellationToken ct = default)
    {
        if (!_client.IsConnected || State != DriverState.Connected)
            return OperationalError.Unavailable("Modbus 未连接");

        var ma = _addressParser.ParseWithCount(point.Address, point.DataType);

        try
        {
            if (ma.Area == ModbusArea.HoldingRegister)
            {
                var registers = EncodeValue(value, point.DataType);
                if (registers.Length == 1)
                    _client.WriteSingleRegister(_unitId, ma.Offset, (short)registers[0]);
                else
                    _client.WriteMultipleRegisters(_unitId, ma.Offset, registers);
            }
            else if (ma.Area == ModbusArea.Coil)
            {
                _client.WriteSingleCoil(_unitId, ma.Offset, Convert.ToBoolean(value));
            }
            else
            {
                return OperationalError.Protocol($"不支持写入功能区: {ma.Area}");
            }

            return OperationResult.Success();
        }
        catch (Exception ex)
        {
            return OperationalError.Protocol($"Modbus 写入失败 [{point.Address}]: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<OperationResult> WriteBatchAsync(
        IEnumerable<KeyValuePair<DevicePoint, object>> entries, CancellationToken ct = default)
    {
        foreach (var (point, value) in entries)
        {
            var r = await WriteAsync(point, value, ct);
            if (r.IsFailure) return r;
        }
        return OperationResult.Success();
    }

    public async ValueTask DisposeAsync()
    {
        if (State == DriverState.Connected)
            await DisconnectAsync();
        _client.Dispose();
    }

    // ---- 内部 ----

    private static IEnumerable<IEnumerable<DevicePoint>> GroupByContinuity(List<DevicePoint> points)
    {
        var parser = new ModbusAddressParser();
        var sorted = points
            .Select(p => (Point: p, Addr: parser.ParseWithCount(p.Address, p.DataType)))
            .OrderBy(x => x.Addr.Area).ThenBy(x => x.Addr.Offset)
            .ToList();

        var groups = new List<List<DevicePoint>>();
        List<DevicePoint>? current = null;
        ModbusAddress? last = null;

        foreach (var (point, addr) in sorted)
        {
            if (current is null || last is null ||
                addr.Area != last.Area || addr.Offset > last.Offset + last.Count)
            {
                current = [];
                groups.Add(current);
            }
            current.Add(point);
            last = addr;
        }
        return groups;
    }

    private ushort[] EncodeValue(object value, DataType type) => type switch
    {
        DataType.Bool => [Convert.ToBoolean(value) ? (ushort)1 : (ushort)0],
        DataType.Byte => [Convert.ToByte(value)],
        DataType.Int16 => [(ushort)Convert.ToInt16(value)],
        DataType.UInt16 => [Convert.ToUInt16(value)],
        DataType.Int32 => EncodeMulti(Convert.ToInt32(value)),
        DataType.UInt32 => EncodeMulti(Convert.ToUInt32(value)),
        DataType.Float => EncodeMulti(Convert.ToSingle(value)),
        DataType.Int64 => EncodeMulti(Convert.ToInt64(value)),
        DataType.UInt64 => EncodeMulti(Convert.ToUInt64(value)),
        DataType.Double => EncodeMulti(Convert.ToDouble(value)),
        _ => throw new NotSupportedException($"不支持的 DataType: {type}")
    };

    private ushort[] EncodeMulti<T>(T value) where T : unmanaged
    {
        var size = System.Runtime.InteropServices.Marshal.SizeOf<T>();
        var bytes = new byte[size];

        // Marshal value into byte array
        var ptr = System.Runtime.InteropServices.Marshal.AllocHGlobal(size);
        try
        {
            System.Runtime.InteropServices.Marshal.StructureToPtr(value!, ptr, false);
            System.Runtime.InteropServices.Marshal.Copy(ptr, bytes, 0, size);
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(ptr);
        }

        // 转换为 ushort[]，FluentModbus 按 Endianness 处理
        var registers = new ushort[size / 2];
        Buffer.BlockCopy(bytes, 0, registers, 0, size);
        return registers;
    }

    private static ModbusEndian ParseEndian(object? value) => value?.ToString()?.ToUpperInvariant() switch
    {
        "CDAB" => ModbusEndian.CDAB,
        "BADC" => ModbusEndian.BADC,
        "DCBA" => ModbusEndian.DCBA,
        _ => ModbusEndian.ABCD
    };
}
