using NitroGateway.Collection;
using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Protocols;
using Xunit;

namespace NitroGateway.UnitTests;

public class PointValuePipelineTests
{
    private readonly Guid _deviceId = Guid.NewGuid();
    private readonly PointValuePipeline _pipeline = new();

    /// <summary>
    /// bool类型不应该经过缩放或偏移处理，应该直接传递原始值。
    /// </summary>
    [Fact]
    public void Bool_Value_Should_Pass_Through()
    {
        var pt = MakePoint(DataType.Bool, 1.0, 0);
        var raw = new RawPointValue { Point = pt, Value = true, Timestamp = DateTime.UtcNow };
        var result = _pipeline.Process(_deviceId, [raw]);
        Assert.Single(result);
        Assert.Equal(true, result[0].Value);
        Assert.Equal(QualityCode.Good, result[0].Quality);
    }
    [Fact]
    public void Numeric_Value_Should_Be_Scaled()
    {
        var pt = MakePoint(DataType.Int16, 2.0, 5);
        var raw = new RawPointValue { Point = pt, Value = 10, Timestamp = DateTime.UtcNow };
        var result = _pipeline.Process(_deviceId, [raw]);
        Assert.Single(result);
        Assert.Equal(25.0, (double)result[0].Value!, 2); // (10 * 2) + 5 = 25
        Assert.Equal(QualityCode.Good, result[0].Quality);
    }
    [Fact]
    public void Float_NoScale_ReturnsEngineeringValue()
    {
        var pt = MakePoint(DataType.Float, 1.0, 0);
        var raw = new RawPointValue { Point = pt, Value = 12.5d, Timestamp = DateTime.UtcNow };
        var result = _pipeline.Process(_deviceId, [raw]);
        Assert.Single(result);
        Assert.Equal(12.5, (double)result[0].Value!, 2);
        Assert.Equal(QualityCode.Good, result[0].Quality);
    }

    [Fact]
    public void Scale_100x01p5_Returns15()
    {
        var pt = MakePoint(DataType.Int16, 0.1, 5);
        var raw = new RawPointValue { Point = pt, Value = 100, Timestamp = DateTime.UtcNow };
        var result = _pipeline.Process(_deviceId, [raw]);
        Assert.Equal(15.0, (double)result[0].Value!, 2);
    }

    [Fact]
    public void Bool_SkipsScale_ReturnsBool()
    {
        var pt = MakePoint(DataType.Bool, 999, 0);
        var raw = new RawPointValue { Point = pt, Value = true, Timestamp = DateTime.UtcNow };
        var result = _pipeline.Process(_deviceId, [raw]);
        Assert.Equal(true, result[0].Value);
    }

    [Fact]
    public void Deadband_FiltersOutSmallChange()
    {
        var pt = MakePoint(DataType.Float, 1.0, 0, deadband: 0.5);
        _pipeline.Process(_deviceId, [MakeRaw(pt, 60.0)]);
        var result = _pipeline.Process(_deviceId, [MakeRaw(pt, 60.25)]);
        Assert.Empty(result);
    }

    [Fact]
    public void Deadband_LargeChangePasses()
    {
        var pt = MakePoint(DataType.Float, 1.0, 0, deadband: 0.5);
        _pipeline.Process(_deviceId, [MakeRaw(pt, 60.0)]);
        var result = _pipeline.Process(_deviceId, [MakeRaw(pt, 75.0)]);
        Assert.Single(result);
    }

    [Fact]
    public void Deadband_Zero_SkipsFilter()
    {
        var pt = MakePoint(DataType.Float, 1.0, 0, deadband: 0);
        _pipeline.Process(_deviceId, [MakeRaw(pt, 60.0)]);
        var result = _pipeline.Process(_deviceId, [MakeRaw(pt, 60.0001)]);
        Assert.Single(result);
    }

    [Fact]
    public void Deadband_NewPipeline_FirstValueAlwaysPasses()
    {
        var pt = MakePoint(DataType.Float, 1.0, 0, deadband: 100);
        var result = _pipeline.Process(_deviceId, [MakeRaw(pt, 42.0)]);
        Assert.Single(result);
    }

    [Fact]
    public void DeviceId_PropagatedToSnapshot()
    {
        var pt = MakePoint(DataType.Int16, 1.0, 0);
        var result = _pipeline.Process(_deviceId, [MakeRaw(pt, 1)]);
        Assert.Equal(_deviceId, result[0].DeviceId);
    }

    [Fact]
    public void RawValue_PreservedInSnapshot()
    {
        var pt = MakePoint(DataType.Int16, 10.0, 0);
        var result = _pipeline.Process(_deviceId, [MakeRaw(pt, 1234)]);
        Assert.Equal(1234, Convert.ToInt32(result[0].RawValue));
        Assert.Equal(12340.0, (double)result[0].Value!, 0);
    }

    private static DevicePoint MakePoint(DataType type, double scale, double offset, double deadband = 0) =>
        new() { Id = Guid.NewGuid(), Name = "test", Address = "40001", DataType = type, ScaleFactor = scale, ScaleOffset = offset, Deadband = deadband };

    private static RawPointValue MakeRaw(DevicePoint pt, object value) =>
        new() { Point = pt, Value = value, Timestamp = DateTime.UtcNow };
}
