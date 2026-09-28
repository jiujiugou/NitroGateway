using NitroGateway.Domain.Devices;
using Opc.Ua;

namespace NitroGateway.Protocols.OpcUa;

/// <summary>
/// OPC UA <see cref="Variant"/> ↔ 领域值映射（纯逻辑，无会话/状态，可单测）。
/// <para>读：<see cref="Variant"/> → int/float→double/bool/string 等领域值；
/// 写：领域 <see cref="DataType"/> → 精确 <see cref="Variant"/>。</para>
/// <para>从 <c>OpcUaDriver</c> 抽出的无状态纯函数，行为不变。</para>
/// </summary>
internal static class OpcUaValueCodec
{
    /// <summary>Variant → 领域值（int/float→double/bool/string 等）。null 回退 0.0（与 Modbus 失败读语义一致）</summary>
    public static object VariantToValue(Variant v) => v.Value switch
    {
        null => 0.0,
        sbyte sb => (short)sb,
        short s => s,
        int i => i,
        long l => l,
        ushort us => us,
        uint ui => ui,
        ulong ul => ul,
        float f => (double)f,
        double d => d,
        bool b => b,
        string str => str,
        _ => v.Value
    };

    /// <summary>
    /// 领域类型 → Variant。显式按 <see cref="DataType"/> 映射：若直接按 .NET 类型映射，
    /// Float 点会发成 Double → 服务端 BadTypeMismatch（实测）。
    /// 转换失败抛 <see cref="InvalidOperationException"/>（由调用方归类）。
    /// </summary>
    public static Variant ToVariant(DataType dataType, object value)
    {
        try
        {
            return dataType switch
            {
                DataType.Bool => new Variant(Convert.ToBoolean(value, System.Globalization.CultureInfo.InvariantCulture)),
                DataType.Byte => new Variant(Convert.ToByte(value, System.Globalization.CultureInfo.InvariantCulture)),
                DataType.Int16 => new Variant(Convert.ToInt16(value, System.Globalization.CultureInfo.InvariantCulture)),
                DataType.UInt16 => new Variant(Convert.ToUInt16(value, System.Globalization.CultureInfo.InvariantCulture)),
                DataType.Int32 => new Variant(Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture)),
                DataType.UInt32 => new Variant(Convert.ToUInt32(value, System.Globalization.CultureInfo.InvariantCulture)),
                DataType.Int64 => new Variant(Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture)),
                DataType.UInt64 => new Variant(Convert.ToUInt64(value, System.Globalization.CultureInfo.InvariantCulture)),
                DataType.Float => new Variant(Convert.ToSingle(value, System.Globalization.CultureInfo.InvariantCulture)),
                DataType.Double => new Variant(Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture)),
                DataType.String => new Variant(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty),
                _ => ToVariant(value)
            };
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"写入值 '{value}' 无法转换为点位类型 {dataType}: {ex.Message}", ex);
        }
    }

    /// <summary>作为 <see cref="ToVariant(DataType, object)"/> 的兜底：按 .NET 实际类型映射（未声明的类型）。
    /// bool/string/数值直接映射，其余经 Convert.ToDouble 兜底。</summary>
    public static Variant ToVariant(object value) => value switch
    {
        bool b => new Variant(b),
        string s => new Variant(s),
        byte by => new Variant(by),
        sbyte sb => new Variant(sb),
        short s => new Variant(s),
        ushort us => new Variant(us),
        int i => new Variant(i),
        uint ui => new Variant(ui),
        long l => new Variant(l),
        ulong ul => new Variant(ul),
        float f => new Variant(f),
        double d => new Variant(d),
        _ => new Variant(Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture))
    };
}
