using NitroGateway.Domain.Devices;

namespace NitroGateway.Protocols.Modbus;

/// <summary>Modbus 地址解析器</summary>
public sealed class ModbusAddressParser : IAddressParser
{
    /// <inheritdoc />
    public PointAddress Parse(string rawAddress)
    {
        if (string.IsNullOrWhiteSpace(rawAddress))
            throw new ArgumentException("地址不能为空", nameof(rawAddress));

        var addr = rawAddress.Trim();

        // 解析功能区前缀
        var (area, offsetStr) = addr[0] switch
        {
            '0' => (ModbusArea.Coil, addr[1..]),
            '1' => (ModbusArea.DiscreteInput, addr[1..]),
            '3' => (ModbusArea.InputRegister, addr[1..]),
            '4' => (ModbusArea.HoldingRegister, addr[1..]),
            _ => throw new ArgumentException($"无法解析功能区前缀: {addr[0]}", nameof(rawAddress))
        };

        if (!int.TryParse(offsetStr, out var offset))
            throw new ArgumentException($"无法解析地址偏移: {offsetStr}", nameof(rawAddress));

        // 默认 PLC 式：地址号 - 1 = 偏移
        var zeroBasedOffset = Math.Max(0, offset - 1);

        return new ModbusAddress(area, (ushort)zeroBasedOffset, 1)
        {
            Raw = rawAddress
        };
    }

    /// <inheritdoc />
    public string Serialize(PointAddress address)
    {
        if (address is not ModbusAddress ma)
            throw new ArgumentException($"不支持此地址类型: {address.GetType().Name}", nameof(address));

        var prefix = ma.Area switch
        {
            ModbusArea.Coil => '0',
            ModbusArea.DiscreteInput => '1',
            ModbusArea.InputRegister => '3',
            ModbusArea.HoldingRegister => '4',
            _ => throw new ArgumentOutOfRangeException(nameof(ma.Area))
        };

        return $"{prefix}{ma.Offset + 1}";
    }

    /// <inheritdoc />
    public int GetDistance(PointAddress a, PointAddress b)
    {
        if (a is not ModbusAddress ma || b is not ModbusAddress mb)
            return -1;

        // 不同功能区不可比
        if (ma.Area != mb.Area)
            return -1;

        // b 在 a 之后，计算间隔
        // 返回 0 表示紧邻（a 最后一个寄存器 + 1 = b 第一个寄存器）
        var aEnd = ma.Offset + ma.Count;
        return mb.Offset - aEnd;
    }

    /// <summary>解析地址时同步填入 DataType 对应的寄存器数量</summary>
    public ModbusAddress ParseWithCount(string rawAddress, DataType dataType)
    {
        var addr = (ModbusAddress)Parse(rawAddress);
        return addr with { Count = DataTypeToRegisterCount(dataType) };
    }

    private static ushort DataTypeToRegisterCount(DataType type) => type switch
    {
        DataType.Bool => 1,
        DataType.Byte => 1,
        DataType.Int16 => 1,
        DataType.UInt16 => 1,
        DataType.Int32 => 2,
        DataType.UInt32 => 2,
        DataType.Float => 2,
        DataType.Int64 => 4,
        DataType.UInt64 => 4,
        DataType.Double => 4,
        DataType.String => 1,  // 最少 1，实际按长度算
        _ => 1
    };
}
