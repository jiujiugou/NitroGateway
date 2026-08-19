using Microsoft.Extensions.Configuration;

namespace NitroGateway.Telemetry;

/// <summary>
/// 追踪导出器类型（Telemetry:Tracing:Exporter）。None 表示保持 dormant（StartActivity 返回 null，仅指标生效）。
/// </summary>
public enum TracingExporterKind
{
    /// <summary>不导出，保持 dormant（向后兼容 ADR-009 的"预留入口"状态）</summary>
    None = 0,

    /// <summary>OTLP（gRPC/HTTP，接 Jaeger / Grafana Tempo / 自建 collector，ADR-056）</summary>
    Otlp = 1,

    /// <summary>Console 输出（本地调试，无需 collector）</summary>
    Console = 2,

    /// <summary>文件输出（JSON Lines 落盘，无需 collector，ADR-057）</summary>
    File = 3
}

/// <summary>OTLP 传输协议（Telemetry:Tracing:Protocol）。</summary>
public enum TracingProtocolKind
{
    /// <summary>gRPC（默认，OTLP 标准端口通常 4317）</summary>
    Grpc = 0,

    /// <summary>HTTP/protobuf（端口通常 4318，适合无 gRPC 的代理环境）</summary>
    HttpProtobuf = 1
}

/// <summary>
/// OpenTelemetry 追踪配置（<c>Telemetry:Tracing</c> 段）。
/// 解析失败或缺省均回退到安全默认值；空 Endpoint 表示交给标准 OTEL_EXPORTER_OTLP_ENDPOINT 环境变量 / 默认 localhost:4317。
/// </summary>
public sealed record TelemetryTracingOptions
{
    /// <summary>是否启用追踪。默认 true（ADR-056：启用执行层）。</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>导出器类型。默认 Otlp。</summary>
    public TracingExporterKind Exporter { get; init; } = TracingExporterKind.Otlp;

    /// <summary>OTLP 端点（仅 Otlp 有效）。空则用 OTEL_EXPORTER_OTLP_ENDPOINT 或默认 http://localhost:4317。</summary>
    public string? Endpoint { get; init; }

    /// <summary>OTLP 协议。默认 Grpc。</summary>
    public TracingProtocolKind Protocol { get; init; } = TracingProtocolKind.Grpc;

    /// <summary>Service Name（Jaeger/Tempo 服务维度）。由 Program 入口传入，缺省 nitrogateway。</summary>
    public string ServiceName { get; init; } = "nitrogateway";

    /// <summary>File 导出器输出目录（仅 File 有效）。默认 logs/traces，按日滚动 traces-yyyyMMdd.jsonl（ADR-057）。</summary>
    public string LogDirectory { get; init; } = "logs/traces";

    /// <summary>File 导出器：按本地日期保留天数，超过的旧文件删除。默认 7；≤0 表示不限（长期落盘有撑爆磁盘风险，仅调试短时使用）。</summary>
    public int MaxRetainedDays { get; init; } = 7;

    /// <summary>File 导出器：单个 jsonl 文件大小上限（字节），超过则滚动到同一天的下一个分段文件（traces-yyyyMMdd-0001.jsonl…）。默认 10 MB；≤0 表示一天一个文件。</summary>
    public long MaxFileBytes { get; init; } = 10 * 1024 * 1024;

    /// <summary>File 导出器：traces 目录总大小上限（字节），超过则删除最旧分段直到达标（当前正在写的文件除外）。默认 512 MB；≤0 表示不限。</summary>
    public long MaxTotalBytes { get; init; } = 512 * 1024 * 1024;

    /// <summary>
    /// 从 <c>Telemetry:Tracing</c> 配置段解析。section 为 null 或字段缺失时保持默认值；
    /// 非法枚举/布尔值静默回退默认（配置错误不阻断启动）。
    /// </summary>
    public static TelemetryTracingOptions Resolve(IConfiguration? section)
    {
        if (section is null) return new TelemetryTracingOptions();

        var o = new TelemetryTracingOptions();
        if (bool.TryParse(section["Enabled"], out var enabled)) o = o with { Enabled = enabled };
        if (Enum.TryParse<TracingExporterKind>(section["Exporter"], ignoreCase: true, out var exporter))
            o = o with { Exporter = exporter };
        var endpoint = section["Endpoint"];
        if (!string.IsNullOrWhiteSpace(endpoint)) o = o with { Endpoint = endpoint };
        if (Enum.TryParse<TracingProtocolKind>(section["Protocol"], ignoreCase: true, out var protocol))
            o = o with { Protocol = protocol };
        var serviceName = section["ServiceName"];
        if (!string.IsNullOrWhiteSpace(serviceName)) o = o with { ServiceName = serviceName };
        var logDirectory = section["LogDirectory"];
        if (!string.IsNullOrWhiteSpace(logDirectory)) o = o with { LogDirectory = logDirectory };
        if (int.TryParse(section["MaxRetainedDays"], out var retainedDays)) o = o with { MaxRetainedDays = retainedDays };
        if (long.TryParse(section["MaxFileBytes"], out var fileBytes)) o = o with { MaxFileBytes = fileBytes };
        if (long.TryParse(section["MaxTotalBytes"], out var totalBytes)) o = o with { MaxTotalBytes = totalBytes };
        return o;
    }
}
