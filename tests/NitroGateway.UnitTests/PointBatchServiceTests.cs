using Microsoft.Extensions.Logging.Abstractions;
using NitroGateway.DeviceManagement;
using NitroGateway.Domain.Devices;
using Xunit;

namespace NitroGateway.UnitTests;

/// <summary>
/// 点位批量服务测试：CSV 导入/导出、名称模板替换、地址自动递增。
///
/// <para>这些功能是现场工程师的日常操作——"从 Excel 导入 500 个点位"。
/// 解析错误会导致点位配置偏差，地址偏移错误会导致读取错误寄存器。</para>
///
/// <para>测试覆盖了 7 个场景：基础 CSV、可选列、格式容错、名称模板、地址递增（Float/Int16）、导出格式。
/// 重点验证边界情况——空输入、无效行跳过、逗号字段转义。</para>
/// </summary>
public class PointBatchServiceTests
{
    private readonly PointBatchService _service = new(NullLogger<PointBatchService>.Instance);
    private readonly Guid _deviceId = Guid.NewGuid();

    // ══════════════════════════════════════════════════
    //  CSV 导入
    // ══════════════════════════════════════════════════

    /// <summary>基础三列（Name, Address, DataType）CSV 解析，验证名称、地址、类型正确解析。</summary>
    [Fact]
    public void ParseCsv_BasicThreeColumns_ParsesCorrectly()
    {
        var csv = "Name,Address,DataType\nTemp1,40001,Float\nPress2,40003,Int16";
        var result = _service.ParseCsv(csv);
        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value!.Count);
        Assert.Equal("Temp1", result.Value[0].Name);
        Assert.Equal("40001", result.Value[0].Address);
        Assert.Equal(DataType.Float, result.Value[0].DataType);
    }

    /// <summary>CSV 包含可选列（ScaleFactor、Deadband、Description）时应正确应用。</summary>
    [Fact]
    public void ParseCsv_WithOptionalColumns_Applied()
    {
        var csv = "Name,Address,DataType,ScaleFactor,Deadband,Description\nTemp,40001,Float,0.5,1.5,炉温";
        var result = _service.ParseCsv(csv);
        Assert.True(result.IsSuccess);
        var p = result.Value![0];
        Assert.Equal(0.5, p.ScaleFactor);
        Assert.Equal(1.5, p.Deadband);
        Assert.Equal("炉温", p.Description);
    }

    /// <summary>
    /// 当 DataType 无法解析时（如拼写错误），该行应被跳过，不污染有效数据。
    /// 工业场景中 Excel 表格的格式错误很常见，不应导致整批导入失败。
    /// </summary>
    [Fact]
    public void ParseCsv_InvalidDataType_SkipsRow()
    {
        var csv = "Name,Address,DataType\nTemp1,40001,Float\nBad,40003,Unknown\nPress2,40005,Int16";
        var result = _service.ParseCsv(csv);
        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value!.Count);
    }

    // ══════════════════════════════════════════════════
    //  名称模板
    // ══════════════════════════════════════════════════

    /// <summary>
    /// 模板 AI_{###} count=3 → AI_001, AI_002, AI_003。
    /// 花括号 {###} 被替换为数字，花括号本身也被移除。
    /// </summary>
    [Fact]
    public void Generate_NameTemplate_PadsWithZeros()
    {
        var points = _service.Generate(_deviceId, "AI_{###}", "40001", 3, DataType.Float);
        Assert.Equal(3, points.Count);
        Assert.Equal("AI_001", points[0].Name);
        Assert.Equal("AI_002", points[1].Name);
        Assert.Equal("AI_003", points[2].Name);
    }

    // ══════════════════════════════════════════════════
    //  地址自动递增（核心：按 DataType.RegisterCount 步进）
    // ══════════════════════════════════════════════════

    /// <summary>
    /// Float 占 2 个 Modbus 寄存器：40001 → 40003 → 40005。
    /// 步长 = 2，不是 1——这是批量生成最容易出 bug 的地方。
    /// </summary>
    [Fact]
    public void Generate_Float_IncrementsByTwo()
    {
        var points = _service.Generate(_deviceId, "P_{###}", "40001", 3, DataType.Float);
        Assert.Equal("40001", points[0].Address);
        Assert.Equal("40003", points[1].Address);
        Assert.Equal("40005", points[2].Address);
    }

    /// <summary>
    /// Int16 占 1 个寄存器：40001 → 40002 → 40003。
    /// 和 Float 对比——不同数据类型步长不同。
    /// </summary>
    [Fact]
    public void Generate_Int16_IncrementsByOne()
    {
        var points = _service.Generate(_deviceId, "P_{###}", "40001", 3, DataType.Int16);
        Assert.Equal("40001", points[0].Address);
        Assert.Equal("40002", points[1].Address);
        Assert.Equal("40003", points[2].Address);
    }

    // ══════════════════════════════════════════════════
    //  边界条件
    // ══════════════════════════════════════════════════

    /// <summary>count=0 应返回空列表，不抛异常。</summary>
    [Fact]
    public void Generate_ZeroCount_ReturnsEmpty()
    {
        var points = _service.Generate(_deviceId, "P_{###}", "40001", 0, DataType.Float);
        Assert.Empty(points);
    }

    // ══════════════════════════════════════════════════
    //  S7 批量生成（ADR-024 P3-3：DB 区按字节步长递增）
    // ══════════════════════════════════════════════════

    /// <summary>S7 Float 占 4 字节：DB1.DBD0 → DBD4 → DBD8。</summary>
    [Fact]
    public void Generate_S7_Float_IncrementsByFourBytes()
    {
        var points = _service.Generate(_deviceId, "P_{###}", "DB1.DBD0", 3, DataType.Float, protocol: "S7");
        Assert.Equal("DB1.DBD0", points[0].Address);
        Assert.Equal("DB1.DBD4", points[1].Address);
        Assert.Equal("DB1.DBD8", points[2].Address);
    }

    /// <summary>S7 Int16 占 2 字节：DB3.DBW0 → DBW2 → DBW4，起始类型与数据类型一致。</summary>
    [Fact]
    public void Generate_S7_Int16_IncrementsByTwoBytes()
    {
        var points = _service.Generate(_deviceId, "P_{###}", "DB3.DBW0", 3, DataType.Int16, protocol: "S7");
        Assert.Equal("DB3.DBW0", points[0].Address);
        Assert.Equal("DB3.DBW2", points[1].Address);
        Assert.Equal("DB3.DBW4", points[2].Address);
    }

    /// <summary>S7 起始地址类型与数据类型不兼容时应显式报错（如 Int16 不能用 DBD）。</summary>
    [Fact]
    public void Generate_S7_TypeMismatch_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            _service.Generate(_deviceId, "P_{###}", "DB1.DBD0", 3, DataType.Int16, protocol: "S7"));
        Assert.Contains("不兼容", ex.Message);
    }

    /// <summary>S7 非法起始地址（非 DB 区格式）应显式报错。</summary>
    [Fact]
    public void Generate_S7_InvalidAddress_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            _service.Generate(_deviceId, "P_{###}", "40001", 3, DataType.Float, protocol: "S7"));
        Assert.Throws<ArgumentException>(() =>
            _service.Generate(_deviceId, "P_{###}", "M100", 3, DataType.Float, protocol: "S7"));
    }

    /// <summary>S7 Bool 位地址不支持批量生成（位步进易错），显式报错并提示手动添加。</summary>
    [Fact]
    public void Generate_S7_Bool_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            _service.Generate(_deviceId, "P_{###}", "DB1.DBX0.0", 3, DataType.Bool, protocol: "S7"));
        Assert.Contains("暂不支持 Bool", ex.Message);
    }

    /// <summary>Modbus 起始地址含非数字内容时应显式报错（回归：int→string 后仍拒绝垃圾输入）。</summary>
    [Fact]
    public void Generate_Modbus_InvalidStartAddress_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            _service.Generate(_deviceId, "P_{###}", "4O001", 3, DataType.Float));
        Assert.Throws<ArgumentException>(() =>
            _service.Generate(_deviceId, "P_{###}", "-1", 3, DataType.Float));
    }

    // ══════════════════════════════════════════════════
    //  CSV 导出
    // ══════════════════════════════════════════════════

    /// <summary>导出 CSV 应包含列头行 + 每个点位一行。</summary>
    [Fact]
    public void ExportCsv_IncludesHeaderAndDataRows()
    {
        var points = new[]
        {
            MakePoint("Temp1", "40001", DataType.Float),
            MakePoint("Press2", "40003", DataType.Int16)
        };
        var csv = _service.ExportCsv(points);
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        Assert.StartsWith("Name,Address,DataType", lines[0]);
    }

    /// <summary>字段包含逗号时应用双引号包裹，保证 CSV 格式合法。</summary>
    [Fact]
    public void ExportCsv_FieldWithComma_WrapsInQuotes()
    {
        var points = new[] { MakePoint("Temp,Top", "40001", DataType.Float) };
        var csv = _service.ExportCsv(points);
        Assert.Contains("\"Temp,Top\"", csv);
    }

    /// <summary>引号包裹的字段（含逗号）应作为一个整体解析，不能被逗号拆开。</summary>
    [Fact]
    public void ParseCsv_QuotedFieldWithComma_ParsesAsSingleField()
    {
        var csv = "Name,Address,DataType,Description\n\"Temp,Top\",40001,Float,\"炉温,1#炉\"";
        var result = _service.ParseCsv(csv);
        Assert.True(result.IsSuccess);
        Assert.Single(result.Value!);
        Assert.Equal("Temp,Top", result.Value![0].Name);
        Assert.Equal("炉温,1#炉", result.Value![0].Description);
    }

    /// <summary>引号转义 "" 应还原为单个引号。</summary>
    [Fact]
    public void ParseCsv_QuotedFieldWithEscapedQuote_Unescapes()
    {
        var csv = "Name,Address,DataType,Description\nTemp,40001,Float,\"他说\"\"好\"\"\"";
        var result = _service.ParseCsv(csv);
        Assert.True(result.IsSuccess);
        Assert.Equal("他说\"好\"", result.Value![0].Description);
    }

    /// <summary>导出→导入往返应完整保留含逗号/引号的字段，Excel 场景闭环。</summary>
    [Fact]
    public void ParseCsv_RoundTrip_PreservesEscapedFields()
    {
        var point = MakePoint("Temp,Top", "40001", DataType.Float);
        point.Description = "炉温,1#炉 \"A\" 区";

        var csv = _service.ExportCsv(new[] { point });
        var result = _service.ParseCsv(csv);

        Assert.True(result.IsSuccess);
        var parsed = result.Value![0];
        Assert.Equal("Temp,Top", parsed.Name);
        Assert.Equal("炉温,1#炉 \"A\" 区", parsed.Description);
        Assert.Equal(DataType.Float, parsed.DataType);
    }

    private static DevicePoint MakePoint(string name, string address, DataType type) => new()
    {
        Id = Guid.NewGuid(), Name = name, Address = address, DataType = type
    };
}

