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
        // ADR-009 P2-4 决策：OpenTelemetry 包保留为"预留入口"（未来接 SDK/导出器用），
        // 当前不接执行层——生产无 ActivityListener/导出器，StartActivity 返回 null，追踪为 dormant 状态；
        // 指标（NitroMetrics）与 /metrics 是当前实际生效的观测契约。
        return services;
    }
}
