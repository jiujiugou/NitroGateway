using System.IO;
using System.Text;
using Prometheus;

namespace NitroGateway.Desktop.Services.Infrastructure;

/// <summary>
/// 进程内指标来源：直接从 prometheus-net 默认注册表抓取，不经过 HTTP / 独立 MetricServer。
/// 因此无需开启 <c>Telemetry:Metrics</c> 也能在桌面端监控页看到指标。
/// </summary>
public sealed class PrometheusMetricsSource : IMetricsSource
{
    /// <inheritdoc />
    public async Task<string> ScrapeAsync(CancellationToken ct = default)
    {
        using var buffer = new MemoryStream();
        await Metrics.DefaultRegistry.CollectAndExportAsTextAsync(buffer, ct).ConfigureAwait(false);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
