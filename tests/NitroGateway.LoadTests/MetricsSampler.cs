using System.Diagnostics;

namespace NitroGateway.LoadTests;

/// <summary>进程级资源快照（GC/内存/CPU），用于计算一段测量窗口内的增量。</summary>
public readonly record struct ProcSnapshot(
    int Gen0, int Gen1, int Gen2, long AllocatedBytes, long WorkingSetBytes, TimeSpan CpuTime)
{
    public static ProcSnapshot Capture()
    {
        using var p = Process.GetCurrentProcess();
        return new ProcSnapshot(
            GC.CollectionCount(0),
            GC.CollectionCount(1),
            GC.CollectionCount(2),
            GC.GetTotalAllocatedBytes(precise: false),
            p.WorkingSet64,
            p.TotalProcessorTime);
    }
}

/// <summary>测量窗口内资源增量的计算结果。</summary>
public readonly record struct ProcDelta(
    int Gen0, int Gen1, int Gen2, double AllocMb, double WorkingSetMb, double CpuPercent)
{
    public static ProcDelta Between(ProcSnapshot start, ProcSnapshot end, double wallSeconds)
    {
        var cpuSeconds = (end.CpuTime - start.CpuTime).TotalSeconds;
        var cpuPercent = wallSeconds <= 0
            ? 0
            : cpuSeconds / (wallSeconds * Environment.ProcessorCount) * 100.0;
        return new ProcDelta(
            end.Gen0 - start.Gen0,
            end.Gen1 - start.Gen1,
            end.Gen2 - start.Gen2,
            (end.AllocatedBytes - start.AllocatedBytes) / 1024.0 / 1024.0,
            end.WorkingSetBytes / 1024.0 / 1024.0,
            cpuPercent);
    }
}
