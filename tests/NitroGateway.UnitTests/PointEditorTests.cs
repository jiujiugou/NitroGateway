using NitroGateway.Desktop.ViewModels;
using NitroGateway.Domain.Devices;
using Xunit;

namespace NitroGateway.UnitTests;

/// <summary>ADR-029 P3：点位表单模型——字段映射与往返。</summary>
public sealed class PointEditorTests
{
    [Fact]
    public void ToPoint_maps_all_fields()
    {
        var editor = new PointEditor
        {
            Name = "温度",
            Address = "40001",
            Description = "炉膛温度",
            DataType = DataType.Int32,
            Enabled = false,
            Access = PointAccess.WriteOnly,
            ScanIntervalMs = 500,
            Deadband = 0.1,
            ScaleFactor = 2.0,
            ScaleOffset = 1.0
        };

        var point = editor.ToPoint();

        Assert.Equal(editor.Id, point.Id);
        Assert.Equal("温度", point.Name);
        Assert.Equal("40001", point.Address);
        Assert.Equal("炉膛温度", point.Description);
        Assert.Equal(DataType.Int32, point.DataType);
        Assert.False(point.Enabled);
        Assert.Equal(PointAccess.WriteOnly, point.Access);
        Assert.Equal(500, point.ScanIntervalMs);
        Assert.Equal(0.1, point.Deadband);
        Assert.Equal(2.0, point.ScaleFactor);
        Assert.Equal(1.0, point.ScaleOffset);
    }

    [Fact]
    public void FromPoint_roundtrip_preserves_all_fields()
    {
        var point = new DevicePoint
        {
            Id = Guid.NewGuid(),
            Name = "转速",
            Address = "DB1.DBD0",
            Description = "主轴转速",
            DataType = DataType.Double,
            Enabled = true,
            Access = PointAccess.ReadWrite,
            ScanIntervalMs = 1000,
            Deadband = 0.5,
            ScaleFactor = 10,
            ScaleOffset = -5
        };

        var roundtrip = PointEditor.FromPoint(point).ToPoint();

        Assert.Equal(point.Id, roundtrip.Id);
        Assert.Equal(point.Name, roundtrip.Name);
        Assert.Equal(point.Address, roundtrip.Address);
        Assert.Equal(point.Description, roundtrip.Description);
        Assert.Equal(DataType.Double, roundtrip.DataType);
        Assert.True(roundtrip.Enabled);
        Assert.Equal(PointAccess.ReadWrite, roundtrip.Access);
        Assert.Equal(1000, roundtrip.ScanIntervalMs);
        Assert.Equal(0.5, roundtrip.Deadband);
        Assert.Equal(10, roundtrip.ScaleFactor);
        Assert.Equal(-5, roundtrip.ScaleOffset);
    }

    [Fact]
    public void PointItem_display_texts()
    {
        var item = PointItem.From(new DevicePoint
        {
            Id = Guid.NewGuid(),
            Name = "温度",
            Address = "40001",
            DataType = DataType.Float,
            Access = PointAccess.ReadOnly,
            ScaleFactor = 1.0,
            ScaleOffset = 0.0
        });

        Assert.Equal("只读", item.AccessText);
        Assert.Equal("1 / 0", item.ScaleText);
        Assert.Equal("继承", item.ScanIntervalText);
        Assert.Equal("是", item.EnabledText);

        var custom = PointItem.From(new DevicePoint
        {
            Id = Guid.NewGuid(),
            Name = "P",
            Address = "40002",
            ScanIntervalMs = 250
        });
        Assert.Equal("250 ms", custom.ScanIntervalText);
    }

    // ===== ADR-037 S4：字段级校验 =====

    [Fact]
    public void Validate_rejects_empty_name_and_address()
    {
        var editor = new PointEditor { Name = "", Address = " " };

        Assert.False(editor.Validate());
        Assert.True(editor.HasErrors);
        Assert.Contains("名称", Assert.Single(editor.GetErrors(nameof(PointEditor.Name)).Cast<string>()));
        Assert.Contains("地址", Assert.Single(editor.GetErrors(nameof(PointEditor.Address)).Cast<string>()));
    }

    [Fact]
    public void Validate_rejects_negative_scan_interval_and_deadband()
    {
        var editor = new PointEditor { Name = "P", Address = "40001", ScanIntervalMs = -1, Deadband = -0.1 };

        Assert.False(editor.Validate());
        Assert.NotEmpty(editor.GetErrors(nameof(PointEditor.ScanIntervalMs)).Cast<string>());
        Assert.NotEmpty(editor.GetErrors(nameof(PointEditor.Deadband)).Cast<string>());
    }

    [Fact]
    public void Validate_rejects_nonfinite_scale_values()
    {
        var editor = new PointEditor { Name = "P", Address = "40001", ScaleFactor = double.NaN, ScaleOffset = double.PositiveInfinity };

        Assert.False(editor.Validate());
        Assert.NotEmpty(editor.GetErrors(nameof(PointEditor.ScaleFactor)).Cast<string>());
        Assert.NotEmpty(editor.GetErrors(nameof(PointEditor.ScaleOffset)).Cast<string>());
    }

    [Fact]
    public void Validate_errors_clear_when_field_fixed()
    {
        var editor = new PointEditor { Name = "", Address = "" };
        Assert.False(editor.Validate());

        editor.Name = "温度";
        editor.Address = "40001";

        Assert.True(editor.Validate());
        Assert.False(editor.HasErrors);
    }
}
