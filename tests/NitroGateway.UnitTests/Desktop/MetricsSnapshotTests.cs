using NitroGateway.Desktop.Services.Infrastructure;
using Xunit;

namespace NitroGateway.UnitTests.Desktop;

/// <summary>
/// 监控页的指标文本解析与派生计算：解析 Prometheus 文本曝光格式、
/// 按标签求和、直方图分位数插值。保证监控页把累计量正确换算为速率 / P95。
/// </summary>
public sealed class MetricsSnapshotTests
{
    private const string Sample = """
        # HELP nitro_collection_total 每设备采集总次数
        # TYPE nitro_collection_total counter
        nitro_collection_total{device="d1",status="success"} 12
        nitro_collection_total{device="d2",status="success"} 8
        nitro_collection_total{device="d1",status="failure"} 2
        # TYPE nitro_mqtt_state gauge
        nitro_mqtt_state 2
        # TYPE nitro_disk_free_bytes gauge
        nitro_disk_free_bytes{path="C:\\data"} 104857600
        nitro_disk_free_bytes{path="D:\\logs"} 52428800
        """;

    [Fact]
    public void Parse_skips_comments_and_reads_labels_and_values()
    {
        var snapshot = MetricsSnapshot.Parse(Sample);

        Assert.Equal(6, snapshot.SampleCount);
        Assert.Equal(22, snapshot.Sum("nitro_collection_total"));
        Assert.Equal(20, snapshot.Sum("nitro_collection_total",
            labels => labels["status"] == "success"));
        Assert.Equal(2, snapshot.Sum("nitro_collection_total",
            labels => labels["status"] == "failure"));
        Assert.Equal(2, snapshot.Value("nitro_mqtt_state"));
    }

    [Fact]
    public void Parse_handles_spaces_and_escaped_chars_in_label_values()
    {
        const string text = "some_metric{msg=\"a b, c\",path=\"x\\\\y\"} 5\n";

        var snapshot = MetricsSnapshot.Parse(text);

        var sample = Assert.Single(snapshot.Samples);
        Assert.Equal("some_metric", sample.Name);
        Assert.Equal("a b, c", sample.Labels["msg"]);
        Assert.Equal("x\\y", sample.Labels["path"]);
        Assert.Equal(5, sample.Value);
    }

    [Fact]
    public void Min_returns_smallest_across_label_sets()
    {
        var snapshot = MetricsSnapshot.Parse(Sample);

        Assert.Equal(52428800, snapshot.Min("nitro_disk_free_bytes"));
    }

    [Fact]
    public void CountValue_counts_samples_equal_to_value()
    {
        const string text = """
            nitro_circuit_breaker_state{device="d1"} 1
            nitro_circuit_breaker_state{device="d2"} 0
            nitro_circuit_breaker_state{device="d3"} 1
            """;

        var snapshot = MetricsSnapshot.Parse(text);

        Assert.Equal(2, snapshot.CountValue("nitro_circuit_breaker_state", 1));
    }

    [Fact]
    public void HistogramQuantile_interpolates_bucket_boundaries()
    {
        // 累积桶：le=5:10, le=10:30, le=25:80, le=50:100；p95 落在 le=50 桶内。
        const string text = """
            nitro_collection_duration_ms_bucket{le="5"} 10
            nitro_collection_duration_ms_bucket{le="10"} 30
            nitro_collection_duration_ms_bucket{le="25"} 80
            nitro_collection_duration_ms_bucket{le="50"} 100
            nitro_collection_duration_ms_bucket{le="+Inf"} 100
            nitro_collection_duration_ms_count 100
            """;

        var snapshot = MetricsSnapshot.Parse(text);

        // 插值：25 + (50-25) * (95-80)/(100-80) = 43.75
        var p95 = snapshot.HistogramQuantile("nitro_collection_duration_ms", 0.95);
        Assert.NotNull(p95);
        Assert.Equal(43.75, p95!.Value, precision: 3);
    }

    [Fact]
    public void HistogramQuantile_returns_null_without_buckets()
    {
        var snapshot = MetricsSnapshot.Parse("nitro_mqtt_state 2\n");

        Assert.Null(snapshot.HistogramQuantile("nitro_collection_duration_ms", 0.95));
    }
}
