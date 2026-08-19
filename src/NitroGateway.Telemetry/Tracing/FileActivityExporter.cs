using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using OpenTelemetry;

namespace NitroGateway.Telemetry.Tracing;

/// <summary>
/// 把已结束的 Activity 以 JSON Lines（.jsonl）追加写入本地滚动文件的导出器（ADR-057）。
/// 供 <c>Telemetry:Tracing:Exporter=File</c> 使用：无需 OTLP collector 即可把 span 落盘归档/排查，
/// 文件可直接打开查看或用 jq/脚本解析。文件按本地日期滚动：{LogDirectory}/traces-yyyyMMdd.jsonl。
/// 磁盘安全（ADR-057 补充）：三档保留策略，防止长期采集写爆磁盘——
///   1) <see cref="TelemetryTracingOptions.MaxRetainedDays"/>：按本地日期保留 N 天，更旧的整文件删除（默认 7 天）；
///   2) <see cref="TelemetryTracingOptions.MaxFileBytes"/>：单文件超限后滚动到同一天的分段文件（默认 10 MB）；
///   3) <see cref="TelemetryTracingOptions.MaxTotalBytes"/>：目录总大小超限后删除最旧分段（默认 512 MB，正在写的文件除外）。
/// 保留清理由内部定时器（每小时）与每次换文件（跨日/超限滚动）触发；Otlp/Console 导出不落本地磁盘，无需保留策略。
/// 线程安全：Export 可能来自后台批次线程，统一走锁；Dispose 时冲刷并关闭写入器。
/// </summary>
internal sealed class FileActivityExporter : BaseExporter<Activity>
{
    private static readonly Regex FileNamePattern = new(
        @"^traces-(\d{8})(?:-(\d{4}))?\.jsonl$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        // 空字段（如无父 span / 无 service.name）不写，保持每行精简
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly TimeSpan PurgeInterval = TimeSpan.FromMinutes(60);

    private readonly object _gate = new();
    private readonly string _directory;
    private readonly TelemetryTracingOptions _options;
    private readonly System.Threading.Timer? _purgeTimer;
    private StreamWriter? _writer;
    private string? _currentDate;
    private int _segment;
    private long _bytesWritten;

    public FileActivityExporter(TelemetryTracingOptions options)
    {
        _options = options;
        _directory = string.IsNullOrWhiteSpace(options.LogDirectory) ? "logs/traces" : options.LogDirectory;
        if (_options.MaxRetainedDays > 0 || _options.MaxTotalBytes > 0)
        {
            // 定时清理：即使进程长期空闲（不触发换文件），过期/超量文件也会被回收
            _purgeTimer = new System.Threading.Timer(PurgeTick, null, PurgeInterval, PurgeInterval);
        }
    }

    public override ExportResult Export(in Batch<Activity> batch)
    {
        foreach (var activity in batch)
        {
            WriteLine(activity);
        }
        return ExportResult.Success;
    }

    private void WriteLine(Activity activity)
    {
        var now = DateTime.Now;
        lock (_gate)
        {
            EnsureWriter(now);
            var json = JsonSerializer.Serialize(ToDto(activity), JsonOptions);
            _writer!.WriteLine(json);
            _bytesWritten += Encoding.UTF8.GetByteCount(json) + Encoding.UTF8.GetByteCount(_writer.NewLine);
        }
    }

    private void PurgeTick(object? state)
    {
        lock (_gate)
        {
            PurgeIfNeeded(DateTime.Now);
        }
    }

    /// <summary>按本地日期/单文件大小滚动打开写入器；跨日时先冲刷旧文件再切新文件。
    /// 大小判断用自维护的 <see cref="_bytesWritten"/>（StreamWriter 缓冲未冲刷时 BaseStream.Length 不准）。</summary>
    private void EnsureWriter(DateTime now)
    {
        var date = now.ToString("yyyyMMdd");
        if (_writer is not null
            && _currentDate == date
            && (_options.MaxFileBytes <= 0 || _bytesWritten < _options.MaxFileBytes))
        {
            return;
        }

        _writer?.Flush();
        _writer?.Dispose();
        _writer = null;
        if (_currentDate != date)
        {
            _currentDate = date;
            _segment = 0;
        }
        else
        {
            _segment++;
        }

        Directory.CreateDirectory(_directory);
        var name = _segment == 0 ? $"traces-{date}.jsonl" : $"traces-{date}-{_segment:D4}.jsonl";
        var path = Path.Combine(_directory, name);
        _writer = new StreamWriter(
            path,
            append: true,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        // 继承文件已存在内容（重启后追写既有分段），从已有大小起计新写入
        _bytesWritten = new FileInfo(path).Length;
        PurgeIfNeeded(now);
    }

    /// <summary>按天保留 + 目录总量两档清理（均在 _gate 锁内调用；当前正在写的文件始终排除）。</summary>
    private void PurgeIfNeeded(DateTime now)
    {
        if (!Directory.Exists(_directory))
        {
            return;
        }

        var files = Directory.GetFiles(_directory, "traces-*.jsonl");
        var currentPath = (_writer?.BaseStream as FileStream)?.Name;

        // 1) 按天保留：删除日期早于 cutoff 的整文件
        if (_options.MaxRetainedDays > 0)
        {
            var cutoff = now.Date.AddDays(-_options.MaxRetainedDays);
            foreach (var file in files)
            {
                if (IsSamePath(file, currentPath))
                {
                    continue;
                }

                var match = FileNamePattern.Match(Path.GetFileName(file));
                if (match.Success
                    && DateTime.TryParseExact(match.Groups[1].Value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var fileDate)
                    && fileDate < cutoff)
                {
                    TryDelete(file);
                }
            }
        }

        // 2) 目录总量：按 (日期, 分段) 升序删最旧，直到总大小不超上限
        if (_options.MaxTotalBytes > 0)
        {
            var ordered = files
                .Where(f => !IsSamePath(f, currentPath))
                .Select(f => (Path: f, Stamp: FileStamp(Path.GetFileName(f))))
                .OrderBy(x => x.Stamp)
                .ToList();

            long total = 0;
            foreach (var x in ordered)
            {
                total += new FileInfo(x.Path).Length;
            }

            foreach (var x in ordered)
            {
                if (total <= _options.MaxTotalBytes)
                {
                    break;
                }

                var length = new FileInfo(x.Path).Length;
                if (TryDelete(x.Path))
                {
                    total -= length;
                }
            }
        }
    }

    private static bool IsSamePath(string a, string? b)
        => b is not null
        && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>从文件名解析 (日期, 分段) 用于排序；解析失败给 (MinValue, int.MaxValue) 使其排最后（不误删未知文件）。</summary>
    private static (DateTime Date, int Segment) FileStamp(string fileName)
    {
        var match = FileNamePattern.Match(fileName);
        if (!match.Success
            || !DateTime.TryParseExact(match.Groups[1].Value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return (DateTime.MinValue, int.MaxValue);
        }

        var segment = match.Groups[2].Success && int.TryParse(match.Groups[2].Value, out var s) ? s : 0;
        return (date, segment);
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (IOException)
        {
            return false; // 被占用/权限不足时跳过，下轮再试
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private object ToDto(Activity activity) => new
    {
        ts = activity.StartTimeUtc.ToString("o"),
        duration_ms = Math.Round(activity.Duration.TotalMilliseconds, 3),
        name = activity.DisplayName,
        kind = activity.Kind.ToString(),
        trace_id = activity.TraceId.ToString(),
        span_id = activity.SpanId.ToString(),
        parent_span_id = activity.ParentSpanId.ToString(),
        status = activity.Status.ToString(),
        service = ServiceName(),
        tags = activity.TagObjects.ToDictionary(t => t.Key, t => t.Value?.ToString())
    };

    /// <summary>从 Resource 取 service.name（由 Program 入口 AddNitroTelemetry 的 serviceName 参数注入）。</summary>
    private object? ServiceName()
    {
        var resource = ParentProvider?.GetResource();
        if (resource is null)
        {
            return null;
        }

        foreach (var attribute in resource.Attributes)
        {
            if (attribute.Key == "service.name")
            {
                return attribute.Value?.ToString();
            }
        }
        return null;
    }

    protected override void Dispose(bool disposing)
    {
        lock (_gate)
        {
            _purgeTimer?.Dispose();
            _writer?.Flush();
            _writer?.Dispose();
            _writer = null;
            _currentDate = null;
            _segment = 0;
        }
        base.Dispose(disposing);
    }
}
