using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using NitroGateway.Collection;

namespace NitroGateway.LoadTests;

/// <summary>单场景测量结果。</summary>
public sealed record ScenarioResult(
    StoreMode Mode,
    int Devices,
    int PointsPerDevice,
    int Concurrency,
    int IntervalMs,
    int Rounds,
    long Produced,
    long Persisted,
    long Drops,
    double Seconds,
    double OfferedPerSec,
    double PersistedPerSec,
    double P50,
    double P95,
    double P99,
    ProcDelta Proc);

/// <summary>背靠背执行采集轮次，测量吞吐/延迟/丢弃/资源。</summary>
public static class LoadRunner
{
    public static async Task<ScenarioResult> RunAsync(
        Harness harness,
        LoadOptions options,
        int deviceCount,
        int pointsPerDevice,
        int concurrency,
        CancellationToken ct = default)
    {
        using var scope = harness.Provider.CreateScope();
        var collector = scope.ServiceProvider.GetRequiredService<IDeviceCollector>();

        await RunForAsync(collector, TimeSpan.FromSeconds(options.WarmupSeconds), options.IntervalMs, ct);

        var produced0 = harness.Reader.RawValuesReturned;
        var persisted0 = harness.StorePointCount();
        var snap0 = ProcSnapshot.Capture();

        var durations = new List<double>(capacity: Math.Max(16, options.Seconds * 8));
        var rounds = 0;
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < options.Seconds)
        {
            var roundSw = Stopwatch.StartNew();
            await collector.CollectOnceAsync(ct);
            roundSw.Stop();
            durations.Add(roundSw.Elapsed.TotalMilliseconds);
            rounds++;

            if (options.IntervalMs > 0)
            {
                var wait = options.IntervalMs - roundSw.Elapsed.TotalMilliseconds;
                if (wait > 0)
                    await Task.Delay((int)wait, ct);
            }
        }

        var seconds = sw.Elapsed.TotalSeconds;

        // 等两个 Channel 排空，再统计落库点数（丢弃 = 产出 - 落库）
        await SettleAsync(harness, ct);

        var snap1 = ProcSnapshot.Capture();
        var produced = harness.Reader.RawValuesReturned - produced0;
        var persisted = harness.StorePointCount() - persisted0;
        var drops = produced - persisted;

        durations.Sort();
        return new ScenarioResult(
            options.Mode,
            Devices: deviceCount,
            PointsPerDevice: pointsPerDevice,
            Concurrency: concurrency,
            IntervalMs: options.IntervalMs,
            Rounds: rounds,
            Produced: produced,
            Persisted: persisted,
            Drops: drops,
            Seconds: seconds,
            OfferedPerSec: seconds > 0 ? produced / seconds : 0,
            PersistedPerSec: seconds > 0 ? persisted / seconds : 0,
            P50: Percentile(durations, 50),
            P95: Percentile(durations, 95),
            P99: Percentile(durations, 99),
            Proc: ProcDelta.Between(snap0, snap1, seconds));
    }

    private static async Task RunForAsync(
        IDeviceCollector collector, TimeSpan duration, int intervalMs, CancellationToken ct)
    {
        if (duration <= TimeSpan.Zero)
            return;

        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < duration)
        {
            var roundSw = Stopwatch.StartNew();
            await collector.CollectOnceAsync(ct);
            roundSw.Stop();

            if (intervalMs > 0)
            {
                var wait = intervalMs - roundSw.Elapsed.TotalMilliseconds;
                if (wait > 0)
                    await Task.Delay((int)wait, ct);
            }
        }
    }

    private static async Task SettleAsync(Harness harness, CancellationToken ct)
    {
        long last = -1;
        var stable = 0;
        for (var i = 0; i < 50; i++)
        {
            await Task.Delay(100, ct);
            var now = harness.StorePointCount();
            if (now == last)
            {
                if (++stable >= 3)
                    return;
            }
            else
            {
                stable = 0;
                last = now;
            }
        }
    }

    private static double Percentile(List<double> sorted, double p)
    {
        if (sorted.Count == 0)
            return 0;
        var idx = (int)Math.Ceiling(p / 100.0 * sorted.Count) - 1;
        return sorted[Math.Clamp(idx, 0, sorted.Count - 1)];
    }
}
