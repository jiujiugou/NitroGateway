using Microsoft.Extensions.Configuration;

namespace NitroGateway.Telemetry;

/// <summary>
/// 指标 HTTP 端点配置（<c>Telemetry:Metrics</c> 段）。
/// Webapi 用 ASP.NET <c>MapMetrics()</c> 暴露 /metrics；无 HTTP 宿主的 Desktop 边缘端用本配置的
/// 独立 <c>MetricServer</c> 暴露，便于现场抓取转发/采集指标。
/// </summary>
public sealed record TelemetryMetricsOptions
{
    /// <summary>配置节名（<c>Telemetry:Metrics</c>）</summary>
    public const string SectionName = "Telemetry:Metrics";

    /// <summary>是否启动独立指标端点；默认 false（不改变既有宿主行为）。</summary>
    public bool Enabled { get; init; }

    /// <summary>监听地址；默认 127.0.0.1（仅本机可访问，避免暴露公网）。</summary>
    public string Host { get; init; } = "127.0.0.1";

    /// <summary>监听端口；默认 9102。</summary>
    public int Port { get; init; } = 9102;

    /// <summary>从配置段解析；缺失/非法保持默认值。</summary>
    public static TelemetryMetricsOptions Resolve(IConfiguration? section)
    {
        var o = new TelemetryMetricsOptions();
        if (section is null) return o;

        if (bool.TryParse(section["Enabled"], out var enabled)) o = o with { Enabled = enabled };
        var host = section["Host"];
        if (!string.IsNullOrWhiteSpace(host)) o = o with { Host = host };
        if (int.TryParse(section["Port"], out var port) && port is > 0 and <= 65535) o = o with { Port = port };
        return o;
    }
}
