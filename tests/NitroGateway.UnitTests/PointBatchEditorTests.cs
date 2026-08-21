using NitroGateway.Desktop.ViewModels;
using NitroGateway.Domain.Devices;
using Xunit;

namespace NitroGateway.UnitTests;

/// <summary>docs/13：点位批量生成表单模型——协议感知起始地址/递增提示、名称预览与校验。</summary>
public sealed class PointBatchEditorTests
{
    [Fact]
    public void DefaultStartAddress_follows_protocol()
    {
        Assert.Equal("40001", PointBatchEditor.DefaultStartAddress("Modbus"));
        Assert.Equal("DB1.DBD0", PointBatchEditor.DefaultStartAddress("S7"));
        Assert.Equal("ns=2;i=1001", PointBatchEditor.DefaultStartAddress("OPC UA"));
    }

    [Fact]
    public void Constructor_initializes_start_address_from_protocol()
    {
        Assert.Equal("40001", new PointBatchEditor { ProtocolName = "Modbus" }.StartAddress);
        Assert.Equal("DB1.DBD0", new PointBatchEditor { ProtocolName = "S7" }.StartAddress);
        Assert.Equal("ns=2;i=1001", new PointBatchEditor { ProtocolName = "OPC UA" }.StartAddress);
    }

    [Fact]
    public void AddressHint_follows_protocol()
    {
        Assert.Equal("如 40001", new PointBatchEditor { ProtocolName = "Modbus" }.AddressHint);
        Assert.Equal("如 DB1.DBD0", new PointBatchEditor { ProtocolName = "S7" }.AddressHint);
        Assert.Equal("如 ns=2;i=1001", new PointBatchEditor { ProtocolName = "OPC UA" }.AddressHint);
    }

    [Fact]
    public void GenHint_describes_increment_rule_by_protocol()
    {
        Assert.Contains("Modbus 寄存器数", new PointBatchEditor { ProtocolName = "Modbus", Count = 100 }.GenHint);
        Assert.Contains("类型字节宽度", new PointBatchEditor { ProtocolName = "S7", Count = 50 }.GenHint);
        Assert.Contains("数值标识（i=）", new PointBatchEditor { ProtocolName = "OPC UA", Count = 30 }.GenHint);
        Assert.Contains("将生成 30 个点位", new PointBatchEditor { ProtocolName = "OPC UA", Count = 30 }.GenHint);
    }

    [Fact]
    public void PreviewName_replaces_first_placeholder_with_001()
    {
        var editor = new PointBatchEditor { NameTemplate = "AI_{###}" };
        Assert.Equal("AI_{001}", editor.PreviewName);

        editor.NameTemplate = "温度";
        Assert.Equal("温度", editor.PreviewName);
    }

    [Fact]
    public void Validate_rejects_empty_template_and_start_address()
    {
        var editor = new PointBatchEditor { NameTemplate = " ", StartAddress = "" };

        Assert.False(editor.Validate());
        Assert.NotEmpty(editor.GetErrors(nameof(PointBatchEditor.NameTemplate)).Cast<string>());
        Assert.NotEmpty(editor.GetErrors(nameof(PointBatchEditor.StartAddress)).Cast<string>());
    }

    [Fact]
    public void Validate_rejects_count_out_of_range()
    {
        Assert.False(new PointBatchEditor { Count = 0 }.Validate());
        Assert.False(new PointBatchEditor { Count = 5001 }.Validate());
        Assert.True(new PointBatchEditor { Count = 100 }.Validate());
    }
}
