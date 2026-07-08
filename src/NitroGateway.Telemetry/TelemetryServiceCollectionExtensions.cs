using Microsoft.Extensions.DependencyInjection;

namespace NitroGateway.Telemetry;

/// <summary>Telemetry 模块 DI 注册</summary>
public static class TelemetryServiceCollectionExtensions
{
    /// <summary>
    /// 注册 Prometheus 指标采集。
    /// 所有指标定义在 <see cref="NitroMetrics"/> 静态类中，由各模块直接上报。
    /// /metrics 端点由调用方通过 <c>app.MapMetrics()</c> 暴露（需引用 prometheus-net.AspNetCore）。
    /// </summary>
    public static IServiceCollection AddNitroTelemetry(this IServiceCollection services)
    {
        // prometheus-net 的 CollectorRegistry 自动管理，无需额外注册
        return services;
    }
}
