using FsCheck;
using FsCheck.Xunit;
using NitroGateway.Protocols.OpcUa;
using NitroGateway.Protocols.S7;
using Xunit;

namespace NitroGateway.UnitTests.PropertyBased;

/// <summary>
/// S7 / OPC UA 地址解析器的属性测试（正确姿势）。
/// <para>生成器从地址<strong>语法</strong>派生，覆盖整个输入域；性质是强不变量与通用安全性质，
/// 不针对某个已知缺陷捏形状。</para>
/// </summary>
public class AddressParserPropertyTests
{
    private readonly OpcUaAddressParser _opcUa = new();

    /// <summary>调用只允许抛 ArgumentException（或其子类），其余异常视为性质被破坏。</summary>
    private static bool OnlyArgumentException(Action action)
    {
        try { action(); return true; }
        catch (ArgumentException) { return true; }
        catch { return false; }
    }

    // ───────────────────────── 通用字符串（整个字符空间） ─────────────────────────

    [Property]
    public bool S7_arbitrary_string_only_throws_ArgumentException(string raw)
        => OnlyArgumentException(() => S7AddressParser.Parse(raw));

    [Property]
    public bool OpcUa_arbitrary_string_only_throws_ArgumentException(string raw)
        => OnlyArgumentException(() => _opcUa.Parse(raw));

    // ───────────────────────── 语法派生：覆盖结构化输入域 ─────────────────────────

    // S7 语法："DB<digits>.DB<letter><digits>" 或 "<M|I|Q><digits>"，数字任意长度
    private static readonly Gen<string> S7GrammarGen =
        Gen.Choose(0, int.MaxValue).Select(n =>
        {
            var d1 = new string((char)('0' + n % 10), (n % 12) + 1);
            var d2 = new string((char)('0' + (n / 10) % 10), (n / 120 % 12) + 1);
            return n % 2 == 0
                ? $"DB{d1}.DB{"BDWX"[(n / 3) % 4]}{d2}"
                : $"{"MIQ"[(n / 5) % 3]}{d1}";
        });

    [Property]
    public Property S7_grammar_only_throws_ArgumentException()
        => Prop.ForAll(Arb.From(S7GrammarGen), (string raw) =>
            OnlyArgumentException(() => S7AddressParser.Parse(raw)));

    // OPC UA 语法："ns=<n>;<s|i|g|b>=<body>"，body 覆盖 base64 字母表与非法字符
    private static readonly Gen<string> OpcUaGrammarGen =
        Gen.Choose(0, int.MaxValue).Select(n =>
        {
            var ns = n % 10;
            var prefix = new[] { "s=", "i=", "g=", "b=" }[n % 4];
            var ch = "a0@="[(n / 4) % 4];
            var body = new string(ch, (n / 16) % 6);
            return $"ns={ns};{prefix}{body}";
        });

    [Property]
    public Property OpcUa_grammar_only_throws_ArgumentException()
        => Prop.ForAll(Arb.From(OpcUaGrammarGen), (string raw) =>
            OnlyArgumentException(() => _opcUa.Parse(raw)));

    // ───────────────────────── 强不变量：往返恒等 ─────────────────────────

    /// <summary>性质：合法 DB 地址解析后字段自洽（DbNumber / ByteOffset / Area）。</summary>
    [Property]
    public void S7_db_address_fields_match(int dbSeed, int offsetSeed)
    {
        var db = Math.Abs(dbSeed % 1000);
        var offset = Math.Abs(offsetSeed % 100000);

        var a = S7AddressParser.Parse($"DB{db}.DBD{offset}");

        Assert.Equal(db, a.DbNumber);
        Assert.Equal(offset, a.ByteOffset);
        Assert.Equal("DB", a.Area);
    }

    /// <summary>性质：数字标识符地址 Serialize 后再 Parse，必须回到同一 NodeId。</summary>
    [Property]
    public void OpcUa_numeric_roundtrip(ushort ns, uint id)
    {
        var address = new OpcUaAddress { Raw = "x", NamespaceIndex = ns, NumericId = id };

        var rt = (OpcUaAddress)_opcUa.Parse(_opcUa.Serialize(address));

        Assert.Equal(ns, rt.NamespaceIndex);
        Assert.Equal(id, rt.NumericId);
    }

    /// <summary>性质：字符串标识符地址 Serialize 后再 Parse，必须回到同一 NodeId（含特殊字符）。</summary>
    [Property]
    public Property OpcUa_string_roundtrip()
    {
        var gen = Gen.Choose(0, int.MaxValue);
        return Prop.ForAll(Arb.From(gen), (int n) =>
        {
            var ns = (ushort)(n % 6);
            var body = new string("a0;"[(n / 6) % 3], (n / 18) % 5);
            var address = new OpcUaAddress { Raw = "x", NamespaceIndex = ns, StringId = body };

            var rt = (OpcUaAddress)_opcUa.Parse(_opcUa.Serialize(address));

            return rt.NamespaceIndex == ns && rt.StringId == body;
        });
    }
}
