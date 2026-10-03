using System.Globalization;
using System.Text;
using NitroGateway.LoadTests;

// ═══════════════════════════════════════════════════════════════
//  NitroGateway L2 采集链路进程内压测
//  假 IDeviceReader → 真实 DeviceCollector/Pipeline/DataDispatcher → Fake/SQLite Store
// ═══════════════════════════════════════════════════════════════

LoadOptions options;
try
{
    options = LoadOptions.Parse(args);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[error] {ex.Message}");
    return 2;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

Console.WriteLine("NitroGateway L2 采集链路压测");
Console.WriteLine($"模式={options.Mode}  设备数={string.Join(",", options.Devices)}  点位/设备={options.PointsPerDevice}  " +
                  $"并发={string.Join(",", options.Concurrencies)}  时长={options.Seconds}s  预热={options.WarmupSeconds}s  轮间隔={options.IntervalMs}ms");
Console.WriteLine(new string('─', 100));

var results = new List<ScenarioResult>();
try
{
    foreach (var deviceCount in options.Devices)
    {
        var devices = PointGenerator.Generate(deviceCount, options.PointsPerDevice);
        foreach (var concurrency in options.Concurrencies)
        {
            Console.Write($"[运行] 设备={deviceCount} 并发={concurrency} ... ");
            await using var harness = Harness.Create(options, devices, concurrency);
            await harness.StartAsync(cts.Token);
            ScenarioResult result;
            try
            {
                result = await LoadRunner.RunAsync(
                    harness, options, deviceCount, options.PointsPerDevice, concurrency, cts.Token);
            }
            finally
            {
                await harness.StopAsync(CancellationToken.None);
            }

            results.Add(result);
            Console.WriteLine(
                $"完成: 产出={result.Produced:N0} 落库={result.Persisted:N0} 丢弃={result.Drops:N0} " +
                $"落库速率={result.PersistedPerSec:N0}/s p95={result.P95:F1}ms");
            Console.WriteLine(new string('─', 100));
        }
    }
}
catch (OperationCanceledException)
{
    Console.WriteLine("\n[中断] 用户取消");
}

PrintTable(results);

if (options.ReportPath is { Length: > 0 } reportPath)
{
    File.WriteAllText(reportPath, BuildMarkdown(options, results), Encoding.UTF8);
    Console.WriteLine($"\n报告已写入: {reportPath}");
}

return results.Count == 0 ? 1 : 0;

static void PrintTable(IReadOnlyList<ScenarioResult> results)
{
    Console.WriteLine();
    Console.WriteLine(
        $"{"设备",6} {"并发",4} {"轮次",5} {"产出",12} {"落库",12} {"丢弃",8} " +
        $"{"落库/s",10} {"p50ms",7} {"p95ms",7} {"p99ms",7} {"Gen0",6} {"Gen1",5} {"Gen2",5} {"分配MB",8} {"内存MB",8} {"CPU%",6}");

    foreach (var r in results)
    {
        Console.WriteLine(
            $"{r.Devices,6} {r.Concurrency,4} {r.Rounds,5} {r.Produced,12:N0} {r.Persisted,12:N0} {r.Drops,8:N0} " +
            $"{r.PersistedPerSec,10:N0} {r.P50,7:F1} {r.P95,7:F1} {r.P99,7:F1} " +
            $"{r.Proc.Gen0,6} {r.Proc.Gen1,5} {r.Proc.Gen2,5} {r.Proc.AllocMb,8:F1} {r.Proc.WorkingSetMb,8:F1} {r.Proc.CpuPercent,6:F1}");
    }
}

static string BuildMarkdown(LoadOptions options, IReadOnlyList<ScenarioResult> results)
{
    var sb = new StringBuilder();
    sb.AppendLine("# NitroGateway L2 采集链路压测报告");
    sb.AppendLine();
    sb.AppendLine(CultureInfo.InvariantCulture, $"- 模式: `{options.Mode}`（A/Fake=隔离 CPU/Channel；B/SQLite=测写天花板）");
    sb.AppendLine(CultureInfo.InvariantCulture, $"- 设备数: {string.Join(", ", options.Devices)}；点位/设备: {options.PointsPerDevice}");
    sb.AppendLine(CultureInfo.InvariantCulture, $"- 并发: {string.Join(", ", options.Concurrencies)}；每场景 {options.Seconds}s（预热 {options.WarmupSeconds}s）；轮间隔 {options.IntervalMs}ms");
    sb.AppendLine(CultureInfo.InvariantCulture, $"- 生成时间: {DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}");
    sb.AppendLine();
    sb.AppendLine("> 丢弃数 = 假 Reader 产出点数 − 落库点数（等 Channel 排空后统计）。");
    sb.AppendLine("> 非目标：协议驱动、驱动池、熔断器、MQTT 转发（见 L3）。");
    sb.AppendLine();
    sb.AppendLine("| 设备 | 点位/设备 | 并发 | 轮次 | 产出 | 落库 | 丢弃 | 产出/s | 落库/s | p50(ms) | p95(ms) | p99(ms) | Gen0 | Gen1 | Gen2 | 分配(MB) | 内存(MB) | CPU% |");
    sb.AppendLine("|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");

    foreach (var r in results)
    {
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"| {r.Devices} | {r.PointsPerDevice} | {r.Concurrency} | {r.Rounds} | {r.Produced:N0} | {r.Persisted:N0} | {r.Drops:N0} | {r.OfferedPerSec:N0} | {r.PersistedPerSec:N0} | {r.P50:F1} | {r.P95:F1} | {r.P99:F1} | {r.Proc.Gen0} | {r.Proc.Gen1} | {r.Proc.Gen2} | {r.Proc.AllocMb:F1} | {r.Proc.WorkingSetMb:F1} | {r.Proc.CpuPercent:F1} |"));
    }

    return sb.ToString();
}
