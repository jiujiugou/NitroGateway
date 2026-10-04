namespace NitroGateway.Desktop.Services.Infrastructure;

/// <summary>
/// 指标来源抽象：抓取一次当前进程的指标曝光文本（Prometheus 文本格式）。
/// 抽成接口便于单测注入固定文本，避免测试依赖真实指标状态。
/// </summary>
public interface IMetricsSource
{
    /// <summary>抓取一次指标文本（Prometheus 文本曝光格式）。</summary>
    Task<string> ScrapeAsync(CancellationToken ct = default);
}
