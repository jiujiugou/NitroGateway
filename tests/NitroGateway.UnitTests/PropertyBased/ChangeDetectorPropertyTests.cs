using FsCheck;
using FsCheck.Xunit;
using NitroGateway.Collection;
using NitroGateway.Domain.Devices;
using Xunit;

namespace NitroGateway.UnitTests.PropertyBased;

/// <summary>
/// 变化抑制器的属性测试。核心性质：放行集合永远是输入的子集（只做筛选，不新增、不改写）。
/// </summary>
public class ChangeDetectorPropertyTests
{
    private static readonly Guid DeviceId = Guid.NewGuid();
    private static readonly Guid[] PointIds = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];

    private static PointSnapshot Snapshot(int n, double deadband) => new()
    {
        DeviceId = DeviceId,
        DevicePointId = PointIds[n % PointIds.Length],
        DataType = DataType.Double,
        Value = (double)(n % 100),
        Quality = n % 5 == 0 ? QualityCode.Bad : QualityCode.Good,
        Deadband = deadband,
        Timestamp = DateTime.UtcNow
    };

    private static List<PointSnapshot> Build(int seed, double deadband)
    {
        var rnd = new System.Random(seed);
        var list = new List<PointSnapshot>();
        var count = rnd.Next(0, 30);
        for (var i = 0; i < count; i++)
            list.Add(Snapshot(rnd.Next(), deadband));
        return list;
    }

    /// <summary>性质：Filter 的输出永远是输入的子集（引用相等），且条数不增。</summary>
    [Property]
    public bool Filter_output_is_subset_of_input(int seed)
    {
        var input = Build(seed, deadband: 0.5);
        var detector = new ChangeDetector(TimeSpan.FromMinutes(5));

        var result = detector.Filter(input, DateTime.UtcNow);

        return result.Count <= input.Count
            && result.All(r => input.Any(x => ReferenceEquals(x, r)));
    }

    /// <summary>性质：Deadband=0 的点位每样本都放行（向后兼容语义）。</summary>
    [Property]
    public bool Deadband_zero_passes_every_sample(int seed)
    {
        var input = Build(seed, deadband: 0);
        var detector = new ChangeDetector(TimeSpan.FromMinutes(5));

        return detector.Filter(input, DateTime.UtcNow).Count == input.Count;
    }
}
