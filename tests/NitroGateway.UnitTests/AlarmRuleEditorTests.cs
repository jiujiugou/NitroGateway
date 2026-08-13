using NitroGateway.Alarm.Domain;
using NitroGateway.Desktop.ViewModels;
using Xunit;

namespace NitroGateway.UnitTests;

/// <summary>
/// ADR-043：告警规则表单编辑模型——设备→点位级联、校验、ToRule/FromRule 双向映射。
/// </summary>
public sealed class AlarmRuleEditorTests
{
    [Fact]
    public void Device_change_refreshes_points_cascade()
    {
        var d1 = TestDevices.Device("D1");
        d1.AddPoint(TestDevices.Point("P1"));
        d1.AddPoint(TestDevices.Point("P2"));
        var d2 = TestDevices.Device("D2");
        d2.AddPoint(TestDevices.Point("P3"));

        var editor = new AlarmRuleEditor(new[] { d1, d2 });

        Assert.Equal(2, editor.Devices.Count);
        Assert.Empty(editor.Points);

        editor.DeviceId = d1.Id;

        Assert.Equal(2, editor.Points.Count);
        Assert.Contains(editor.Points, p => p.Name == "P1");
        Assert.Contains(editor.Points, p => p.Name == "P2");

        editor.DeviceId = d2.Id;

        Assert.Single(editor.Points);
        Assert.Contains(editor.Points, p => p.Name == "P3");
    }

    [Fact]
    public void Validate_requires_device_and_point()
    {
        var d1 = TestDevices.Device("D1");
        d1.AddPoint(TestDevices.Point("P1"));
        var editor = new AlarmRuleEditor(new[] { d1 });

        Assert.False(editor.Validate());
        Assert.True(editor.HasErrors);
        Assert.NotEmpty(editor.GetErrors("DeviceId"));
        Assert.NotEmpty(editor.GetErrors("PointId"));
    }

    [Fact]
    public void Validate_passes_when_fields_valid()
    {
        var d1 = TestDevices.Device("D1");
        d1.AddPoint(TestDevices.Point("P1"));
        var editor = new AlarmRuleEditor(new[] { d1 })
        {
            DeviceId = d1.Id,
            PointId = d1.Points.First().Id,
            Operator = ">",
            Threshold = 80
        };

        Assert.True(editor.Validate());
        Assert.False(editor.HasErrors);
    }

    [Fact]
    public void Validate_rejects_between_with_upper_below_lower()
    {
        var d1 = TestDevices.Device("D1");
        d1.AddPoint(TestDevices.Point("P1"));
        var editor = new AlarmRuleEditor(new[] { d1 })
        {
            DeviceId = d1.Id,
            PointId = d1.Points.First().Id,
            Operator = "Between",
            Threshold = 50,
            ThresholdUpper = 10
        };

        Assert.False(editor.Validate());
        Assert.NotEmpty(editor.GetErrors("ThresholdUpper"));
    }

    [Fact]
    public void Validate_rejects_negative_duration()
    {
        var d1 = TestDevices.Device("D1");
        d1.AddPoint(TestDevices.Point("P1"));
        var editor = new AlarmRuleEditor(new[] { d1 })
        {
            DeviceId = d1.Id,
            PointId = d1.Points.First().Id,
            DurationSeconds = -1
        };

        Assert.False(editor.Validate());
        Assert.NotEmpty(editor.GetErrors("DurationSeconds"));
    }

    [Fact]
    public void ToRule_maps_fields_and_clears_upper_when_not_between()
    {
        var d1 = TestDevices.Device("D1");
        var p1 = TestDevices.Point("P1");
        d1.AddPoint(p1);
        var editor = new AlarmRuleEditor(new[] { d1 })
        {
            Id = Guid.NewGuid(),
            DeviceId = d1.Id,
            PointId = p1.Id,
            Operator = ">=",
            Threshold = 100,
            ThresholdUpper = 200,
            DurationSeconds = 5,
            Severity = AlarmSeverity.Critical,
            MessageTemplate = "{value} 高",
            Enabled = false
        };

        var rule = editor.ToRule();

        Assert.Equal(editor.Id, rule.Id);
        Assert.Equal(d1.Id, rule.DeviceId);
        Assert.Equal(p1.Id, rule.PointId);
        Assert.Equal(">=", rule.Operator);
        Assert.Equal(100, rule.Threshold);
        Assert.Null(rule.ThresholdUpper); // 非 Between 清空上限，与 Web 保存逻辑一致
        Assert.Equal(5, rule.DurationSeconds);
        Assert.Equal(AlarmSeverity.Critical, rule.Severity);
        Assert.Equal("{value} 高", rule.MessageTemplate);
        Assert.False(rule.Enabled);
    }

    [Fact]
    public void ToRule_between_keeps_upper()
    {
        var d1 = TestDevices.Device("D1");
        d1.AddPoint(TestDevices.Point("P1"));
        var editor = new AlarmRuleEditor(new[] { d1 })
        {
            DeviceId = d1.Id,
            PointId = d1.Points.First().Id,
            Operator = "Between",
            Threshold = 10,
            ThresholdUpper = 50
        };

        var rule = editor.ToRule();

        Assert.True(editor.IsBetween);
        Assert.Equal(10, rule.Threshold);
        Assert.Equal(50, rule.ThresholdUpper);
    }

    [Fact]
    public void FromRule_roundtrips_and_refreshes_points()
    {
        var d1 = TestDevices.Device("D1");
        var p1 = TestDevices.Point("P1");
        d1.AddPoint(p1);
        d1.AddPoint(TestDevices.Point("P2"));
        var d2 = TestDevices.Device("D2");
        d2.AddPoint(TestDevices.Point("P3"));
        var rule = new AlarmRule
        {
            Id = Guid.NewGuid(),
            DeviceId = d1.Id,
            PointId = p1.Id,
            Operator = "Between",
            Threshold = 10,
            ThresholdUpper = 50,
            DurationSeconds = 3,
            Severity = AlarmSeverity.Emergency,
            MessageTemplate = "t",
            Enabled = false
        };

        var editor = AlarmRuleEditor.FromRule(rule, new[] { d1, d2 });

        Assert.Equal(rule.Id, editor.Id);
        Assert.Equal(rule.DeviceId, editor.DeviceId);
        Assert.Equal(rule.PointId, editor.PointId);
        Assert.Equal("Between", editor.Operator);
        Assert.Equal(10, editor.Threshold);
        Assert.Equal(50, editor.ThresholdUpper);
        Assert.Equal(3, editor.DurationSeconds);
        Assert.Equal(AlarmSeverity.Emergency, editor.Severity);
        Assert.Equal("t", editor.MessageTemplate);
        Assert.False(editor.Enabled);
        Assert.True(editor.IsBetween);
        Assert.Equal(2, editor.Points.Count); // d1 有 2 个点位，级联已刷新
    }
}
