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
        var points = _service.Generate(_deviceId, "AI_{###}", 40001, 3, DataType.Float);
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
        var points = _service.Generate(_deviceId, "P_{###}", 40001, 3, DataType.Float);
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
        var points = _service.Generate(_deviceId, "P_{###}", 40001, 3, DataType.Int16);
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
        var points = _service.Generate(_deviceId, "P_{###}", 40001, 0, DataType.Float);
        Assert.Empty(points);
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

    private static DevicePoint MakePoint(string name, string address, DataType type) => new()
    {
        Id = Guid.NewGuid(), Name = name, Address = address, DataType = type
    };
}
