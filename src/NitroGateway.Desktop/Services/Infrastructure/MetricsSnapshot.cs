using System.Globalization;
using System.Text;

namespace NitroGateway.Desktop.Services.Infrastructure;

/// <summary>单条指标样本：一次抓取中的一个 series（指标名 + 标签集合 + 当前值）。</summary>
public sealed record MetricSample(string Name, IReadOnlyDictionary<string, string> Labels, double Value);

/// <summary>
/// 一次指标抓取（Prometheus 文本曝光格式）的解析结果。
/// 提供求和 / 取值 / 计数 / 直方图分位数等派生能力，供监控页把原始累计量换算成人可读的速率与延迟。
/// </summary>
public sealed class MetricsSnapshot
{
    private static readonly IReadOnlyDictionary<string, string> EmptyLabels = new Dictionary<string, string>();

    private readonly List<MetricSample> _samples;

    private MetricsSnapshot(List<MetricSample> samples) => _samples = samples;

    /// <summary>本次抓取的全部样本。</summary>
    public IReadOnlyList<MetricSample> Samples => _samples;

    /// <summary>样本条数（用于页面反馈抓取规模）。</summary>
    public int SampleCount => _samples.Count;

    /// <summary>解析 Prometheus 文本曝光格式；畸形行跳过、不抛异常（监控不应因单行异常中断）。</summary>
    public static MetricsSnapshot Parse(string? text)
    {
        var samples = new List<MetricSample>();
        if (string.IsNullOrWhiteSpace(text))
            return new MetricsSnapshot(samples);

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
                continue;

            // 分离 series 与值：标签值内可能含空格，故用最后一个 '}' 定位标签段（无标签时用首个空格）。
            string series;
            string rest;
            var brace = line.LastIndexOf('}');
            if (brace >= 0)
            {
                series = line[..(brace + 1)];
                rest = line[(brace + 1)..].Trim();
            }
            else
            {
                var space = line.IndexOf(' ');
                if (space < 0)
                    continue;
                series = line[..space];
                rest = line[(space + 1)..].Trim();
            }

            if (rest.Length == 0)
                continue;

            // 取第一个 token 作为值，忽略可选的 timestamp。
            var valueToken = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
            if (!TryParseValue(valueToken, out var value))
                continue;

            ParseSeries(series, out var name, out var labels);
            if (name.Length > 0)
                samples.Add(new MetricSample(name, labels, value));
        }

        return new MetricsSnapshot(samples);
    }

    /// <summary>对指定指标名（可加标签过滤）求和。</summary>
    public double Sum(string name, Func<IReadOnlyDictionary<string, string>, bool>? filter = null)
    {
        var total = 0d;
        foreach (var s in _samples)
        {
            if (s.Name == name && (filter is null || filter(s.Labels)))
                total += s.Value;
        }

        return total;
    }

    /// <summary>取指定指标名（可加标签过滤）的第一个样本值；无匹配返回 null。</summary>
    public double? Value(string name, Func<IReadOnlyDictionary<string, string>, bool>? filter = null)
    {
        foreach (var s in _samples)
        {
            if (s.Name == name && (filter is null || filter(s.Labels)))
                return s.Value;
        }

        return null;
    }

    /// <summary>取指定指标名（可加标签过滤）的最小样本值；无匹配返回 null。用于多路径 gauge（如磁盘剩余）。</summary>
    public double? Min(string name, Func<IReadOnlyDictionary<string, string>, bool>? filter = null)
    {
        double? min = null;
        foreach (var s in _samples)
        {
            if (s.Name == name && (filter is null || filter(s.Labels)))
                min = min is null ? s.Value : Math.Min(min.Value, s.Value);
        }

        return min;
    }

    /// <summary>统计指定指标名下值等于 <paramref name="value"/> 的样本条数。</summary>
    public int CountValue(string name, double value)
    {
        var n = 0;
        foreach (var s in _samples)
        {
            if (s.Name == name && s.Value == value)
                n++;
        }

        return n;
    }

    /// <summary>
    /// 计算直方图分位数（Prometheus 标准线性插值）。
    /// 传入不带 <c>_bucket</c> 后缀的指标名（如 <c>nitro_collection_duration_ms</c>）；无桶数据返回 null。
    /// </summary>
    public double? HistogramQuantile(string name, double quantile)
    {
        var bucketName = name + "_bucket";
        var buckets = new List<(double Upper, double Cumulative)>();
        foreach (var s in _samples)
        {
            if (s.Name != bucketName || !s.Labels.TryGetValue("le", out var le))
                continue;
            if (!TryParseValue(le, out var upper) || double.IsPositiveInfinity(upper))
                continue;
            buckets.Add((upper, s.Value));
        }

        if (buckets.Count == 0)
            return null;

        buckets.Sort((a, b) => a.Upper.CompareTo(b.Upper));

        var total = 0d;
        foreach (var s in _samples)
        {
            if (s.Name == name + "_count")
                total += s.Value;
        }

        if (total <= 0)
            total = buckets[^1].Cumulative;
        if (total <= 0)
            return null;

        var rank = quantile * total;
        var prevUpper = 0d;
        var prevCumulative = 0d;
        foreach (var (upper, cumulative) in buckets)
        {
            if (cumulative >= rank)
            {
                if (cumulative == prevCumulative)
                    return upper;
                var fraction = (rank - prevCumulative) / (cumulative - prevCumulative);
                return prevUpper + (upper - prevUpper) * fraction;
            }

            prevUpper = upper;
            prevCumulative = cumulative;
        }

        return prevUpper;
    }

    private static void ParseSeries(string series, out string name, out IReadOnlyDictionary<string, string> labels)
    {
        var brace = series.IndexOf('{');
        if (brace < 0)
        {
            name = series;
            labels = EmptyLabels;
            return;
        }

        name = series[..brace];
        labels = ParseLabels(series[(brace + 1)..].TrimEnd('}'));
    }

    private static IReadOnlyDictionary<string, string> ParseLabels(string inner)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var i = 0;
        while (i < inner.Length)
        {
            var eq = inner.IndexOf('=', i);
            if (eq < 0)
                break;

            var key = inner[i..eq].Trim().Trim(',').Trim();
            i = eq + 1;
            if (i >= inner.Length || inner[i] != '"')
                break;

            i++; // 跳过起始引号
            var sb = new StringBuilder();
            while (i < inner.Length && inner[i] != '"')
            {
                if (inner[i] == '\\' && i + 1 < inner.Length)
                {
                    var next = inner[i + 1];
                    sb.Append(next switch { 'n' => '\n', 't' => '\t', _ => next });
                    i += 2;
                }
                else
                {
                    sb.Append(inner[i]);
                    i++;
                }
            }

            i++; // 跳过结束引号
            if (key.Length > 0)
                result[key] = sb.ToString();

            while (i < inner.Length && inner[i] != ',')
                i++;
            i++;
        }

        return result;
    }

    private static bool TryParseValue(string token, out double value)
    {
        switch (token)
        {
            case "+Inf":
                value = double.PositiveInfinity;
                return true;
            case "-Inf":
                value = double.NegativeInfinity;
                return true;
            case "NaN":
                value = double.NaN;
                return true;
        }

        return double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
}
