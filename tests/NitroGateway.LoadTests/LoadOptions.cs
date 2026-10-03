using System.Globalization;

namespace NitroGateway.LoadTests;

/// <summary>落库模式：假 Store（隔离 CPU/Channel）或真实 SQLite（测写天花板）。</summary>
public enum StoreMode
{
    Fake,
    Sqlite
}

/// <summary>L2 进程内压测命令行参数。</summary>
public sealed class LoadOptions
{
    public StoreMode Mode { get; private set; } = StoreMode.Fake;

    /// <summary>设备数扫描列表（每场景重建 Harness）。</summary>
    public List<int> Devices { get; } = [50];

    public int PointsPerDevice { get; private set; } = 50;

    /// <summary>单轮并发扫描列表（映射 Collection:MaxConcurrency）。</summary>
    public List<int> Concurrencies { get; } = [5, 20, 100];

    /// <summary>每场景测量时长（秒）。</summary>
    public int Seconds { get; private set; } = 20;

    /// <summary>测量前预热时长（秒），让 JIT/GC 稳定。</summary>
    public int WarmupSeconds { get; private set; } = 3;

    /// <summary>轮间隔（毫秒）；0=背靠背全速（找饱和吞吐）。</summary>
    public int IntervalMs { get; private set; }

    public string? ReportPath { get; private set; }

    public static LoadOptions Parse(string[] args)
    {
        var o = new LoadOptions();
        for (int i = 0; i < args.Length; i++)
        {
            string Need()
            {
                if (i + 1 >= args.Length) throw new ArgumentException($"缺少参数值: {args[i]}");
                return args[++i];
            }

            switch (args[i].ToLowerInvariant())
            {
                case "--mode":
                    o.Mode = Need().ToLowerInvariant() switch
                    {
                        "a" or "fake" => StoreMode.Fake,
                        "b" or "sqlite" => StoreMode.Sqlite,
                        var other => throw new ArgumentException($"未知 --mode: {other}（A|B）")
                    };
                    break;
                case "--devices":
                    o.Devices.Clear();
                    o.Devices.AddRange(ParseIntList(Need(), "--devices"));
                    break;
                case "--points":
                    o.PointsPerDevice = int.Parse(Need(), CultureInfo.InvariantCulture);
                    break;
                case "--concurrency":
                    o.Concurrencies.Clear();
                    o.Concurrencies.AddRange(ParseIntList(Need(), "--concurrency"));
                    break;
                case "--seconds":
                    o.Seconds = int.Parse(Need(), CultureInfo.InvariantCulture);
                    break;
                case "--warmup":
                    o.WarmupSeconds = int.Parse(Need(), CultureInfo.InvariantCulture);
                    break;
                case "--interval-ms":
                    o.IntervalMs = int.Parse(Need(), CultureInfo.InvariantCulture);
                    break;
                case "--report":
                    o.ReportPath = Need();
                    break;
                case "-h" or "--help" or "/?":
                    PrintHelp();
                    Environment.Exit(0);
                    break;
                default:
                    throw new ArgumentException($"未知参数: {args[i]}");
            }
        }

        if (o.Devices.Any(d => d <= 0)) throw new ArgumentException("--devices 必须为正整数");
        if (o.PointsPerDevice <= 0) throw new ArgumentException("--points 必须为正整数");
        if (o.Concurrencies.Any(c => c <= 0)) throw new ArgumentException("--concurrency 必须为正整数");
        if (o.Seconds <= 0) throw new ArgumentException("--seconds 必须为正整数");
        if (o.WarmupSeconds < 0) throw new ArgumentException("--warmup 不能为负");
        if (o.IntervalMs < 0) throw new ArgumentException("--interval-ms 不能为负");
        return o;
    }

    private static IEnumerable<int> ParseIntList(string csv, string name)
    {
        foreach (var part in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                throw new ArgumentException($"{name} 含非法整数: {part}");
            yield return v;
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            NitroGateway L2 采集链路进程内压测

            用法:
              dotnet run -c Release --project tests/NitroGateway.LoadTests -- [选项]

            选项:
              --mode A|B           A=假 Store（默认，测 CPU/Channel）；B=真实 SQLite（测写天花板）
              --devices 50,100     设备数扫描列表（逗号分隔，默认 50）
              --points 50          每设备点位数（默认 50）
              --concurrency 5,20   单轮并发扫描列表（默认 5,20,100）
              --seconds 20         每场景测量时长秒（默认 20）
              --warmup 3           预热时长秒（默认 3）
              --interval-ms 0      轮间隔毫秒；0=背靠背全速（默认 0）
              --report out.md      追加 Markdown 报告
              -h / --help          帮助

            说明: 用假 IDeviceReader 绕开协议层，直接压 DeviceCollector→Pipeline→DataDispatcher→Store。
                  丢弃数 = 假 Reader 产出点数 − 落库点数（等 Channel 排空后统计）。
            """);
    }
}
