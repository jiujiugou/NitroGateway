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
    public static IServiceCollection AddNitroTelemetry(this IServiceCollection services)
    {
        // prometheus-net 的 CollectorRegistry 自动管理，无需额外注册

        // 争用+线程池+GC+JIT+网络+异常，默认 Counters 低开销级别）。
        // 注意：StartCollecting() 无幂等守卫——每进程只应调用一次（Webapi / Ingest 为独立进程各自调用）；
        // 返回的 IDisposable 必须强引用保活（其内部持有事件监听器与 24h 回收任务，若被 GC 回收会停采），
        // 因此存入静态字段 _runtimeStats，进程存活期间不释放。
        StartRuntimeStats();
        return services;
    }

    public static IServiceCollection AddNitroTelemetry(
        this IServiceCollection services, IConfiguration? configuration, string? serviceName = null)
    {
        services.AddNitroTelemetry();

        var options = TelemetryTracingOptions.Resolve(configuration?.GetSection("Telemetry:Tracing"));
        if (!options.Enabled || options.Exporter == TracingExporterKind.None)
        {
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

    private static IDisposable? _runtimeStats;

    private static void StartRuntimeStats()
    {
        if (_runtimeStats == null)
        {
            _runtimeStats = DotNetRuntimeStatsBuilder.Default().StartCollecting();
        }
    }
}
