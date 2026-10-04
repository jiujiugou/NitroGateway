using Microsoft.Extensions.Logging.Abstractions;
using NitroGateway.Desktop.Services.Infrastructure;
using NitroGateway.Desktop.ViewModels;
using Xunit;

namespace NitroGateway.UnitTests.Desktop.ViewModels;

/// <summary>
/// 系统监控页：从注入的指标文本源解析并填充分组行。
/// 覆盖 gauge / 状态映射 / 熔断计数，以及 Counter 速率需要两次采样才出值。
/// </summary>
public sealed class MetricsViewModelTests
{
    private const string Sample = """
        nitro_collection_total{device="d1",status="success"} 10
        nitro_collection_total{device="d1",status="failure"} 1
        nitro_mqtt_state 2
        nitro_devices_online 3
        nitro_devices_available 5
        nitro_circuit_breaker_state{device="d1"} 1
        nitro_circuit_breaker_state{device="d2"} 1
        """;

    [Fact]
    public void Refresh_populates_gauge_and_state_rows()
    {
        var vm = Create(Sample);
        using (vm)
        {
            Assert.Equal("已连接", Row(vm, "MQTT 状态").Value);
            Assert.Equal("3", Row(vm, "设备在线").Value);
            Assert.Equal("5", Row(vm, "待采设备").Value);
            Assert.Equal("2", Row(vm, "熔断中设备").Value);
        }
    }

    [Fact]
    public async Task Counter_rate_needs_two_samples()
    {
        var source = new StubMetricsSource(Sample);
        var vm = Create(source);
        using (vm)
        {
            // 首次采样无上一快照：速率行占位
            Assert.Equal("—", Row(vm, "采集设备速率").Value);

            source.Text = Sample.Replace("success\"} 10", "success\"} 30");
            await vm.RefreshCommand.ExecuteAsync(null);

            // 有增量：速率已出值（具体数值依赖真实流逝时间，这里只验证不再是占位）
            Assert.NotEqual("—", Row(vm, "采集设备速率").Value);
        }
    }

    [Fact]
    public void Freshness_row_reports_age_from_latest_sample_timestamp()
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0 - 10;
        var text = $"nitro_latest_sample_timestamp_seconds {ts.ToString(System.Globalization.CultureInfo.InvariantCulture)}\n";

        var vm = Create(text);
        using (vm)
        {
            var value = int.Parse(Row(vm, "数据新鲜度").Value, System.Globalization.CultureInfo.InvariantCulture);
            Assert.InRange(value, 9, 12);
        }
    }

    [Fact]
    public void Timer_is_started_and_stopped_on_dispose()
    {
        var timer = new FakeUiTimer();
        var vm = new MetricsViewModel(
            new StubMetricsSource(Sample), new UiDispatcher(),
            NullLogger<MetricsViewModel>.Instance, timer);

        Assert.True(timer.IsStarted);
        vm.Dispose();
        Assert.False(timer.IsStarted);
    }

    private static MetricsViewModel Create(string text) => new(
        new StubMetricsSource(text), new UiDispatcher(),
        NullLogger<MetricsViewModel>.Instance, new FakeUiTimer());

    private static MetricsViewModel Create(IMetricsSource source) => new(
        source, new UiDispatcher(), NullLogger<MetricsViewModel>.Instance, new FakeUiTimer());

    private static MetricRow Row(MetricsViewModel vm, string name) =>
        vm.Groups.SelectMany(g => g.Rows).First(r => r.Name == name);

    private sealed class StubMetricsSource(string text) : IMetricsSource
    {
        public string Text { get; set; } = text;

        public Task<string> ScrapeAsync(CancellationToken ct = default) => Task.FromResult(Text);
    }
}
