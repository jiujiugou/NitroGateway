using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Prometheus.DotNetRuntime;

namespace NitroGateway.Telemetry;

/// <summary>Telemetry 模块 DI 注册</summary>
public static class TelemetryServiceCollectionExtensions
{
    /// <summary>
    /// 注册 Prometheus 指标采集（不含追踪）。
    /// 业务指标定义在 <see cref="NitroMetrics"/> 静态类中，由各模块直接上报；
    /// 运行时指标（GC/线程池/锁争用/JIT/网络/异常，前缀 dotnet_）由
    /// <see cref="DotNetRuntimeStatsBuilder"/> 自动汇入同一全局注册表（ADR-049），
    /// /metrics 一次暴露，不改变现有指标契约。
    /// /metrics 端点由调用方通过 <c>app.MapMetrics()</c> 暴露（需引用 prometheus-net.AspNetCore）。
    /// 注意：本重载不启用追踪；需要 OpenTelemetry 追踪请使用带 IConfiguration 的重载（ADR-056）。
    /// </summary>
    public static IServiceCollection AddNitroTelemetry(this IServiceCollection services)
    {
        // prometheus-net 的 CollectorRegistry 自动管理，无需额外注册
        // ADR-056：追踪已启用（见带 IConfiguration 的重载）；指标（NitroMetrics）与 /metrics 仍是核心观测契约。

        // ADR-049：启动 System.Runtime EventCounters 采集（DotNetRuntimeStatsBuilder.Default() =
        // 争用+线程池+GC+JIT+网络+异常，默认 Counters 低开销级别）。
        // 注意：StartCollecting() 无幂等守卫——每进程只应调用一次（Webapi / Ingest 为独立进程各自调用）；
        // 返回的 IDisposable 必须强引用保活（其内部持有事件监听器与 24h 回收任务，若被 GC 回收会停采），
        // 因此存入静态字段 _runtimeStats，进程存活期间不释放。
        StartRuntimeStats();
        return services;
    }

    /// <summary>
    /// 注册 Prometheus 指标采集 + OpenTelemetry 追踪导出（ADR-056）。
    /// 追踪读取 <c>Telemetry:Tracing</c> 配置段（见 <see cref="TelemetryTracingOptions.Resolve"/>）：
    /// <list type="bullet">
    ///   <item><c>Enabled</c>：默认 true（启用执行层，各模块已埋点 StartActivity 真正产生 span）</item>
    ///   <item><c>Exporter</c>：Otlp（默认）/ Console / File / None（None=保持 dormant）</item>
    ///   <item><c>Endpoint</c>：OTLP 端点，空则用 OTEL_EXPORTER_OTLP_ENDPOINT 或默认 http://localhost:4317</item>
    ///   <item><c>Protocol</c>：Grpc（默认）/ HttpProtobuf</item>
    ///   <item><c>ServiceName</c>：Jaeger/Tempo 服务维度；优先取 <paramref name="serviceName"/> 参数</item>
    ///   <item><c>LogDirectory</c>：File 导出器输出目录（默认 logs/traces，按日滚动 .jsonl，ADR-057）</item>
    ///   <item><c>MaxRetainedDays</c>：File 按天保留（默认 7，≤0 不限）；<c>MaxFileBytes</c> 单文件滚动（默认 10MB）；
    ///     <c>MaxTotalBytes</c> 目录总量上限（默认 512MB）——防止长期采集写爆磁盘（ADR-057）</item>
    /// </list>
    /// 启用时把全局 ActivitySource（<see cref="Tracing.GatewayActivitySource.Name"/>）接入 TracerProvider，
    /// 导出到所选后端；生产无 collector 时可设 Enabled=false 或 Exporter=None 关闭（避免后台重连日志）。
    /// </summary>
    public static IServiceCollection AddNitroTelemetry(
        this IServiceCollection services, IConfiguration? configuration, string? serviceName = null)
    {
        services.AddNitroTelemetry();

        var options = TelemetryTracingOptions.Resolve(configuration?.GetSection("Telemetry:Tracing"));
        if (!options.Enabled || options.Exporter == TracingExporterKind.None)
        {
            // 未启用：保持 ADR-009 的 dormant 状态（无 ActivityListener/导出器，StartActivity 返回 null）。
            return services;
        }

        services.AddOpenTelemetry().WithTracing(builder =>
        {
            builder.SetResourceBuilder(ResourceBuilder.CreateDefault()
                .AddService(serviceName ?? options.ServiceName));
            builder.AddSource(Tracing.GatewayActivitySource.Name);
            switch (options.Exporter)
            {
                case TracingExporterKind.Otlp:
                    builder.AddOtlpExporter(o =>
                    {
                        if (!string.IsNullOrWhiteSpace(options.Endpoint))
                            o.Endpoint = new Uri(options.Endpoint);
                        if (options.Protocol == TracingProtocolKind.HttpProtobuf)
                            o.Protocol = OtlpExportProtocol.HttpProtobuf;
                    });
                    break;
                case TracingExporterKind.Console:
                    builder.AddConsoleExporter();
                    break;
                case TracingExporterKind.File:
                    builder.AddProcessor(new SimpleActivityExportProcessor(
                        new Tracing.FileActivityExporter(options)));
                    break;
            }
        });
        return services;
    }

    /// <summary>进程级运行时指标采集句柄（ADR-049）。强引用保活：事件监听器/24h 回收任务依赖它不被 GC 回收。</summary>
    private static IDisposable? _runtimeStats;

    private static void StartRuntimeStats()
    {
        if (_runtimeStats == null)
        {
            _runtimeStats = DotNetRuntimeStatsBuilder.Default().StartCollecting();
        }
    }
}
