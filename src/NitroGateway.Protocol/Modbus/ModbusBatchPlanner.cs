namespace NitroGateway.Protocols.Modbus;

internal static class ModbusBatchPlanner
{
    /// <summary>
    /// 把按 offset 升序的点位切分为连续段，返回每段在原列表中的元素索引。
    /// 连续 = 下一点 offset == 前一点 offset + 前一点寄存器数。
    /// </summary>
    /// <param name="sorted">按 offset 升序的 (offset, 寄存器数) 列表</param>
    public static List<List<int>> SplitContiguousSegments(IReadOnlyList<(int Offset, int Count)> sorted)
    {
        var segments = new List<List<int>>();
        if (sorted.Count == 0)
            return segments;

        var current = new List<int> { 0 };
        for (var i = 1; i < sorted.Count; i++)
        {
            if (sorted[i].Offset == sorted[i - 1].Offset + sorted[i - 1].Count)
                current.Add(i);
            else
            {
                segments.Add(current);
                current = new List<int> { i };
            }
        }
        segments.Add(current);
        return segments;
    }
}
