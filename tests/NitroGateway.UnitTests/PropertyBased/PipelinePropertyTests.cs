using FsCheck;
using FsCheck.Xunit;
using NitroGateway.Collection;
using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Protocols;
using Xunit;

namespace NitroGateway.UnitTests.PropertyBased;

/// <summary>
/// 值转换管道的属性测试。
/// <para>性质均为<strong>强不变量</strong>，生成器覆盖整个 DataType × 原始值空间，
/// 不针对某个已知缺陷捏形状。</para>
/// </summary>
public class PipelinePropertyTests
{
    private readonly Guid _deviceId = Guid.NewGuid();
    private readonly PointValuePipeline _pipeline = new();

    private static readonly DataType[] AllTypes = Enum.GetValues<DataType>();

    private static DevicePoint Point(DataType type, double scale, double offset, double deadband = 0) =>
        new()
        {
            Id = Guid.NewGuid(),
            Name = "p",
            Address = "40001",
            DataType = type,
            ScaleFactor = scale,
            ScaleOffset = offset,
            Deadband = deadband
        };

    /// <summary>从种子派生一个原始值（含 null / 非数字字符串 / 布尔 / 数值等各分支）。</summary>
    private static object ValueFor(int n) => (n % 6) switch
    {
        0 => null!,
        1 => "abc",
        2 => "12.5",
        3 => true,
        4 => (object)(n % 1000),
        _ => 12.5
    };

    /// <summary>性质 1：输出条数恒等于输入条数（每个原始值恰好产出一个快照）。</summary>
    [Property]
    public bool Process_preserves_count(int seed)
    {
        var rnd = new System.Random(seed);
        var raws = new List<RawPointValue>();
        var count = rnd.Next(0, 30);
        for (var i = 0; i < count; i++)
        {
            var n = rnd.Next();
            raws.Add(new RawPointValue
            {
                Point = Point(AllTypes[n % AllTypes.Length], 1.0, 0, deadband: 0.5),
                Value = ValueFor(n),
                Timestamp = DateTime.UtcNow
            });
        }

        var result = _pipeline.Process(_deviceId, raws);
        return result.Count == raws.Count;
    }

    /// <summary>性质 2：数值型、ScaleFactor=1/ScaleOffset=0 时，工程值恒等于原始数值。</summary>
    [Property]
    public bool Identity_scale_returns_raw_value(int v)
    {
        double expected = v;
        var raw = new RawPointValue
        {
            Point = Point(DataType.Double, 1.0, 0),
            Value = expected,
            Timestamp = DateTime.UtcNow
        };

        var result = _pipeline.Process(_deviceId, [raw]);
        return result.Count == 1
            && result[0].Value is double d
            && d.Equals(expected)
            && result[0].Quality == QualityCode.Good;
    }

    /// <summary>性质 3：Bool/String 不做缩放，原值透传（缩放参数应被忽略）。</summary>
    [Property]
    public bool Non_numeric_passes_through(bool isBool)
    {
        var type = isBool ? DataType.Bool : DataType.String;
        object value = isBool ? true : "text";
        var raw = new RawPointValue
        {
            Point = Point(type, 999, 999),
            Value = value,
            Timestamp = DateTime.UtcNow
        };

        var result = _pipeline.Process(_deviceId, [raw]);
        return result.Count == 1 && Equals(result[0].Value, value);
    }
}
