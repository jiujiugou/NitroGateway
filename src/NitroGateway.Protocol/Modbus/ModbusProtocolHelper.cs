using NitroGateway.Domain.Devices;

namespace NitroGateway.Protocol.Modbus;

/// <summary>
/// Modbus 协议工具类。TCP 和 RTU 驱动共享的解码/分组/编码逻辑。
/// </summary>
public static class ModbusProtocolHelper
{
    /// <summary>按功能区和地址连续性分组，每组可合并为一次批量读请求</summary>
    public static IEnumerable<IEnumerable<DevicePoint>> GroupByContinuity(List<DevicePoint> points)
    {
        var parser = new ModbusAddressParser();
        var sorted = points.Select(p => (Point: p, Addr: parser.ParseWithCount(p.Address, p.DataType)))
            .OrderBy(x => x.Addr.Area).ThenBy(x => x.Addr.Offset).ToList();
        var groups = new List<List<DevicePoint>>();
        List<DevicePoint>? cur = null; ModbusAddress? last = null;
        foreach (var (p, a) in sorted)
        {
            if (cur is null || last is null || a.Area != last.Area || a.Offset > last.Offset + last.Count) { cur = []; groups.Add(cur); }
            cur.Add(p); last = a;
        }
        return groups;
    }

    /// <summary>Modbus 寄存器 → 领域值（int/float/bool/string）。大端字节序，驱动内解码</summary>
    public static object DecodeRegisters(ushort[] registers, DataType type)
    {
        if (registers.Length == 0) return 0;
        var bytes = new byte[registers.Length * 2];
        for (var i = 0; i < registers.Length; i++) { bytes[i * 2] = (byte)(registers[i] >> 8); bytes[i * 2 + 1] = (byte)(registers[i] & 0xFF); }
        if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
        return type switch
        {
            DataType.Bool => registers[0] != 0, DataType.Byte => bytes[0],
            DataType.Int16 => (short)registers[0], DataType.UInt16 => registers[0],
            DataType.Int32 when bytes.Length >= 4 => BitConverter.ToInt32(bytes),
            DataType.UInt32 when bytes.Length >= 4 => BitConverter.ToUInt32(bytes),
            DataType.Float when bytes.Length >= 4 => (double)BitConverter.ToSingle(bytes),
            DataType.Int64 when bytes.Length >= 8 => BitConverter.ToInt64(bytes),
            DataType.UInt64 when bytes.Length >= 8 => BitConverter.ToUInt64(bytes),
            DataType.Double when bytes.Length >= 8 => BitConverter.ToDouble(bytes),
            DataType.String => System.Text.Encoding.ASCII.GetString(bytes).TrimEnd('\0'),
            _ => registers
        };
    }

    /// <summary>工程值 → Modbus 寄存器数组（编码写入）</summary>
    public static ushort[] EncodeValue(object value, DataType type) => type switch
    {
        DataType.Bool => [(ushort)(Convert.ToBoolean(value) ? 1 : 0)],
        DataType.Byte => [Convert.ToByte(value)],
        DataType.Int16 => [(ushort)Convert.ToInt16(value)],
        DataType.UInt16 => [Convert.ToUInt16(value)],
        DataType.Int32 => EncodeMulti(Convert.ToInt32(value)),
        DataType.UInt32 => EncodeMulti(Convert.ToUInt32(value)),
        DataType.Float => EncodeMulti(Convert.ToSingle(value)),
        DataType.Int64 => EncodeMulti(Convert.ToInt64(value)),
        DataType.UInt64 => EncodeMulti(Convert.ToUInt64(value)),
        DataType.Double => EncodeMulti(Convert.ToDouble(value)),
        _ => throw new NotSupportedException($"DataType: {type}")
    };

    /// <summary>从连接参数解析字节序，默认 ABCD</summary>
    public static ModbusEndian ParseEndian(object? value) => value?.ToString()?.ToUpperInvariant() switch
    {
        "CDAB" => ModbusEndian.CDAB, "BADC" => ModbusEndian.BADC, "DCBA" => ModbusEndian.DCBA, _ => ModbusEndian.ABCD
    };

    private static ushort[] EncodeMulti(float f) { var b = BitConverter.GetBytes(f); if (BitConverter.IsLittleEndian) Array.Reverse(b); return [(ushort)((b[0] << 8) | b[1]), (ushort)((b[2] << 8) | b[3])]; }
    private static ushort[] EncodeMulti(int v) => EncodeMulti((float)v);
    private static ushort[] EncodeMulti(uint v) => EncodeMulti((float)v);
    private static ushort[] EncodeMulti(double d) { var b = BitConverter.GetBytes(d); if (BitConverter.IsLittleEndian) Array.Reverse(b); return [(ushort)((b[0] << 8) | b[1]), (ushort)((b[2] << 8) | b[3]), (ushort)((b[4] << 8) | b[5]), (ushort)((b[6] << 8) | b[7])]; }
    private static ushort[] EncodeMulti(long v) => EncodeMulti((double)v);
    private static ushort[] EncodeMulti(ulong v) => EncodeMulti((double)v);
}
