using NitroGateway.Collection;
using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Protocols;
using Xunit;

namespace NitroGateway.UnitTests.Collection;

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
    public void Deadband_SmallChange_PassesThrough_ButDoesNotRefreshCache()
    {
        var pt = MakePoint(DataType.Float, 1.0, 0, deadband: 0.5);
        _pipeline.Process(_deviceId, [MakeRaw(pt, 60.0)]);
        // 死区只影响缓存（供告警 Duration 使用），数据照常往下游传输
        var result = _pipeline.Process(_deviceId, [MakeRaw(pt, 60.25)]);
        Assert.Single(result);
        Assert.Equal(60.25, (double)result[0].Value!, 2);
        // 缓存未刷新，仍保持上一基准值
        Assert.Equal(60.0, _pipeline.GetLastValue(pt.Id)!.Value, 2);
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

    [Fact]
    public void PointName_PropagatedToSnapshot()
    {
        var pt = MakePoint(DataType.Int16, 1.0, 0);
        var result = _pipeline.Process(_deviceId, [MakeRaw(pt, 1)]);
        Assert.Equal("test", result[0].PointName);
    }

    [Fact]
    public void DataType_PropagatedToSnapshot()
    {
        var pt = MakePoint(DataType.Bool, 1.0, 0);
        var result = _pipeline.Process(_deviceId, [MakeRaw(pt, true)]);
        Assert.Equal(DataType.Bool, result[0].DataType);
    }

    /// <summary>快照携带点位读写权限，供转发 payload 透传、云端自动注册识别可写点位</summary>
    [Fact]
    public void Access_PropagatedToSnapshot()
    {
        var pt = new DevicePoint
        {
            Id = Guid.NewGuid(),
            Name = "test",
            Address = "40001",
            DataType = DataType.Float,
            ScaleFactor = 1.0,
            ScaleOffset = 0,
            Access = PointAccess.ReadWrite
        };
        var result = _pipeline.Process(_deviceId, [MakeRaw(pt, 12.5d)]);
        Assert.Equal(PointAccess.ReadWrite, result[0].Access);

        // 默认只读：未配置 Access 的点位透传 ReadOnly
        var ro = MakePoint(DataType.Float, 1.0, 0);
        var roResult = _pipeline.Process(_deviceId, [MakeRaw(ro, 12.5d)]);
        Assert.Equal(PointAccess.ReadOnly, roResult[0].Access);
    }

    /// <summary>所有白名单数值类型都必须参与缩放（IsNumericType 的每个 true 分支）。</summary>
    [Theory]
    [InlineData(DataType.Int16, 10)]
    [InlineData(DataType.UInt16, 10)]
    [InlineData(DataType.Int32, 10)]
    [InlineData(DataType.UInt32, 10)]
    [InlineData(DataType.Int64, 10L)]
    [InlineData(DataType.UInt64, 10UL)]
    [InlineData(DataType.Float, 10.0)]
    [InlineData(DataType.Double, 10.0)]
    public void NumericTypes_AreScaled(DataType type, object rawValue)
    {
        var pt = MakePoint(type, 2.0, 5.0);
        var snap = _pipeline.Process(_deviceId, [MakeRaw(pt, rawValue)])[0];
        Assert.Equal(25.0, (double)snap.Value!, 3); // 10 * 2 + 5
        Assert.Equal(QualityCode.Good, snap.Quality);
        Assert.Equal(type, snap.DataType);
    }

    /// <summary>Byte 不在 IsNumericType 白名单 → 直通不缩放（记录当前行为；是否应缩放见评审）。</summary>
    [Fact]
    public void Byte_PassesThroughWithoutScaling()
    {
        var pt = MakePoint(DataType.Byte, 2.0, 5.0);
        var snap = _pipeline.Process(_deviceId, [MakeRaw(pt, (byte)10)])[0];
        Assert.Equal((byte)10, Assert.IsType<byte>(snap.Value));
        Assert.Equal((byte)10, Assert.IsType<byte>(snap.RawValue));
    }

    /// <summary>Bool 分支：快照全字段透传（DevicePointId/Name/Access/Timestamp/RawValue/Deadband）。</summary>
    [Fact]
    public void BoolSnapshot_PropagatesAllFields()
    {
        var ts = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var pt = MakePoint(DataType.Bool, 1.0, 0, deadband: 0.5);
        pt.Access = PointAccess.ReadWrite;
        var raw = new RawPointValue { Point = pt, Value = true, Timestamp = ts };

        var snap = _pipeline.Process(_deviceId, [raw])[0];

        Assert.Equal(_deviceId, snap.DeviceId);
        Assert.Equal(pt.Id, snap.DevicePointId);
        Assert.Equal("test", snap.PointName);
        Assert.Equal(DataType.Bool, snap.DataType);
        Assert.Equal(PointAccess.ReadWrite, snap.Access);
        Assert.Equal(ts, snap.Timestamp);
        Assert.Equal(true, snap.Value);
        Assert.Equal(true, snap.RawValue);
        Assert.Equal(QualityCode.Good, snap.Quality);
        Assert.Equal(0.5, snap.Deadband);
    }

    /// <summary>数值分支：快照全字段透传（含 Deadband 与时间戳）。</summary>
    [Fact]
    public void NumericSnapshot_PropagatesAllFields()
    {
        var ts = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var pt = MakePoint(DataType.Float, 1.0, 0, deadband: 0.5);
        pt.Access = PointAccess.ReadWrite;
        var raw = new RawPointValue { Point = pt, Value = 12.5d, Timestamp = ts };

        var snap = _pipeline.Process(_deviceId, [raw])[0];

        Assert.Equal(_deviceId, snap.DeviceId);
        Assert.Equal(pt.Id, snap.DevicePointId);
        Assert.Equal("test", snap.PointName);
        Assert.Equal(DataType.Float, snap.DataType);
        Assert.Equal(PointAccess.ReadWrite, snap.Access);
        Assert.Equal(ts, snap.Timestamp);
        Assert.Equal(12.5, (double)snap.Value!, 3);
        Assert.Equal(12.5, (double)snap.RawValue!, 3);
        Assert.Equal(QualityCode.Good, snap.Quality);
        Assert.Equal(0.5, snap.Deadband);
    }

    /// <summary>缩放失败（值不可转数值）→ Uncertain 快照 + 错误消息，不抛异常、不丢字段。</summary>
    [Fact]
    public void NumericType_UnconvertibleValue_ReturnsUncertainSnapshot()
    {
        var ts = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var pt = MakePoint(DataType.Float, 2.0, 5.0, deadband: 0.5);
        var raw = new RawPointValue { Point = pt, Value = "not-a-number", Timestamp = ts };

        var snap = _pipeline.Process(_deviceId, [raw])[0];

        Assert.Equal(QualityCode.Uncertain, snap.Quality);
        Assert.Null(snap.Value);
        Assert.Equal("not-a-number", snap.RawValue);
        Assert.Equal(pt.Id, snap.DevicePointId);
        Assert.Equal(ts, snap.Timestamp);
        Assert.Equal(0.5, snap.Deadband);
        Assert.False(string.IsNullOrEmpty(snap.ErrorMessage));
    }

    /// <summary>超死区放行后，告警用缓存必须刷新为新基准值（供 Duration 判定）。</summary>
    [Fact]
    public void Deadband_LargeChange_RefreshesCache()
    {
        var pt = MakePoint(DataType.Float, 1.0, 0, deadband: 0.5);
        _pipeline.Process(_deviceId, [MakeRaw(pt, 60.0)]);
        _pipeline.Process(_deviceId, [MakeRaw(pt, 75.0)]);
        Assert.Equal(75.0, _pipeline.GetLastValue(pt.Id)!.Value, 3);
    }

    /// <summary>Deadband=0：缓存每样本都刷新。</summary>
    [Fact]
    public void Deadband_Zero_RefreshesCacheEachSample()
    {
        var pt = MakePoint(DataType.Float, 1.0, 0, deadband: 0);
        _pipeline.Process(_deviceId, [MakeRaw(pt, 60.0)]);
        _pipeline.Process(_deviceId, [MakeRaw(pt, 60.0001)]);
        Assert.Equal(60.0001, _pipeline.GetLastValue(pt.Id)!.Value, 6);
    }

    /// <summary>未记录的点位 GetLastValue 返回 null。</summary>
    [Fact]
    public void GetLastValue_UnknownPoint_ReturnsNull()
    {
        Assert.Null(_pipeline.GetLastValue(Guid.NewGuid()));
    }

    /// <summary>SetLastValue / GetLastValue 往返。</summary>
    [Fact]
    public void SetLastValue_ThenGet_ReturnsValue()
    {
        var pointId = Guid.NewGuid();
        _pipeline.SetLastValue(pointId, 3.14);
        Assert.Equal(3.14, _pipeline.GetLastValue(pointId)!.Value, 3);
    }

    private static DevicePoint MakePoint(DataType type, double scale, double offset, double deadband = 0) =>
        new() { Id = Guid.NewGuid(), Name = "test", Address = "40001", DataType = type, ScaleFactor = scale, ScaleOffset = offset, Deadband = deadband };

    private static RawPointValue MakeRaw(DevicePoint pt, object value) =>
        new() { Point = pt, Value = value, Timestamp = DateTime.UtcNow };
}
