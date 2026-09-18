using FsCheck;
using FsCheck.Xunit;
using NitroGateway.Protocols.Modbus;
using Xunit;

namespace NitroGateway.UnitTests.PropertyBased;

/// <summary>
/// 属性测试示例（FsCheck）：不写"输入 → 期望"的样例，而是声明"对所有输入都必须成立的性质"，
/// 由工具生成大量（默认 100）随机输入去逼反例。
/// <para>与样例测试的区别：样例测试只能覆盖你想到的输入；属性测试覆盖你没想到的。</para>
/// </summary>
public class ModbusAddressPropertyTests
{
    private readonly ModbusAddressParser _parser = new();

    private static readonly ModbusArea[] Areas =
    [
        ModbusArea.Coil,
        ModbusArea.DiscreteInput,
        ModbusArea.InputRegister,
        ModbusArea.HoldingRegister
    ];

    /// <summary>性质 1：Serialize 后再 Parse，必须回到同一地址（功能区 + 零基偏移）。</summary>
    [Property]
    public void Serialize_then_Parse_is_identity(byte areaIndex, ushort offset)
    {
        var address = new ModbusAddress(Areas[areaIndex % Areas.Length], offset, 1) { Raw = "prop" };

        var roundTripped = (ModbusAddress)_parser.Parse(_parser.Serialize(address));

        Assert.Equal(address.Area, roundTripped.Area);
        Assert.Equal(address.Offset, roundTripped.Offset);
    }

    /// <summary>
    /// 性质 2：Parse 对任意字符串，要么成功、要么只抛 <see cref="ArgumentException"/>，
    /// 绝不抛越界/溢出等别的异常；且成功解析的结果必须能稳定往返。
    /// </summary>
    [Property]
    public void Parse_throws_only_ArgumentException(string raw)
    {
        try
        {
            var parsed = (ModbusAddress)_parser.Parse(raw);
            var again = (ModbusAddress)_parser.Parse(_parser.Serialize(parsed));
            Assert.Equal(parsed.Area, again.Area);
            Assert.Equal(parsed.Offset, again.Offset);
        }
        catch (ArgumentException)
        {
            // 合法的失败路径
        }
    }

    /// <summary>定向生成"合法前缀 + 符号/空格 + 数字"的形状，专门逼 Parse 暴露宽松接受。</summary>
    private static readonly Gen<string> MalformedGen =
        Gen.Choose(0, int.MaxValue).Select(n =>
        {
            var prefix = "0134"[n % 4];
            var sign = "+- "[(n / 4) % 3];
            var digits = ((n / 12) % 100000).ToString(System.Globalization.CultureInfo.InvariantCulture);
            return $"{prefix}{sign}{digits}";
        });

    /// <summary>
    /// 性质 3：Parse 只应接受"前缀 [0134] + 纯十进制数字"这一种规范形式。
    /// <para>反例预期：像 "4+1"、"4 1" 这种带符号/空白的串会被 <c>int.TryParse</c> 默认样式接受。</para>
    /// </summary>
    [Property]
    public Property Parse_accepts_only_canonical_numeric_form()
    {
        return Prop.ForAll(Arb.From(MalformedGen), (string raw) =>
        {
            try { _parser.Parse(raw); }
            catch (ArgumentException) { return true; }

            var canonical = raw.Trim();
            return canonical.Length >= 2
                && "0134".Contains(canonical[0])
                && canonical[1..].All(char.IsAsciiDigit);
        });
    }
}
