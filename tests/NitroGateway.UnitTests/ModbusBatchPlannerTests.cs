using NitroGateway.Protocols.Modbus;
using Xunit;

namespace NitroGateway.UnitTests;

/// <summary>
/// ADR-003 P1-1：同类型点位只有寄存器连续才能合并为一次批量读，
/// 非连续段必须切分，否则从首点连读会把间隔寄存器误读成后序点位。
/// </summary>
public class ModbusBatchPlannerTests
{
    [Fact]
    public void SplitContiguousSegments_Empty_ReturnsEmpty()
    {
        var segments = ModbusBatchPlanner.SplitContiguousSegments([]);
        Assert.Empty(segments);
    }

    [Fact]
    public void SplitContiguousSegments_Contiguous_OneSegment()
    {
        // Int16 连续：40001,40002,40003
        var segments = ModbusBatchPlanner.SplitContiguousSegments([(0, 1), (1, 1), (2, 1)]);
        var seg = Assert.Single(segments);
        Assert.Equal([0, 1, 2], seg);
    }

    [Fact]
    public void SplitContiguousSegments_FloatWithGap_Splits()
    {
        // Float(2 寄存器)：40001 与 40005 之间隔了 40003-40004（其他类型/空寄存器）→ 2 段
        var segments = ModbusBatchPlanner.SplitContiguousSegments([(0, 2), (4, 2)]);
        Assert.Equal(2, segments.Count);
        Assert.Equal([0], segments[0]);
        Assert.Equal([1], segments[1]);
    }

    [Fact]
    public void SplitContiguousSegments_Mixed_SplitsCorrectly()
    {
        // Int16 连续两段：(0,1)(1,1) 连续；(3,1)(4,1) 连续；(2,1) 与两侧都有间隙 → 各自成段
        var segments = ModbusBatchPlanner.SplitContiguousSegments([(0, 1), (1, 1), (3, 1), (4, 1)]);
        Assert.Equal(2, segments.Count);
        Assert.Equal([0, 1], segments[0]);
        Assert.Equal([2, 3], segments[1]);
    }
}
