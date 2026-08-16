using Microsoft.Extensions.DependencyInjection;
using Prometheus.DotNetRuntime;

namespace NitroGateway.Telemetry;

/// <summary>Telemetry 模块 DI 注册</summary>
public static class TelemetryServiceCollectionExtensions
{
    /// <summary>
    /// 注册 Prometheus 指标采集。
    /// 业务指标定义在 <see cref="NitroMetrics"/> 静态类中，由各模块直接上报；
    /// 运行时指标（GC/线程池/锁争用/JIT/网络/异常，前缀 dotnet_）由
    /// <see cref="DotNetRuntimeStatsBuilder"/> 自动汇入同一全局注册表（ADR-049），
    /// /metrics 一次暴露，不改变现有指标契约。
    /// /metrics 端点由调用方通过 <c>app.MapMetrics()</c> 暴露（需引用 prometheus-net.AspNetCore）。
    /// </summary>
    public static IServiceCollection AddNitroTelemetry(this IServiceCollection services)
    {
        // prometheus-net 的 CollectorRegistry 自动管理，无需额外注册
        // ADR-009 P2-4 决策：OpenTelemetry 包保留为"预留入口"（未来接 SDK/导出器用），
        // 当前不接执行层——生产无 ActivityListener/导出器，StartActivity 返回 null，追踪为 dormant 状态；
        // 指标（NitroMetrics）与 /metrics 是当前实际生效的观测契约。

        // ADR-049：启动 System.Runtime EventCounters 采集（DotNetRuntimeStatsBuilder.Default() =
        // 争用+线程池+GC+JIT+网络+异常，默认 Counters 低开销级别）。
        // 注意：StartCollecting() 无幂等守卫——每进程只应调用一次（Webapi / Ingest 为独立进程各自调用）；
        // 返回的 IDisposable 必须强引用保活（其内部持有事件监听器与 24h 回收任务，若被 GC 回收会停采），
        // 因此存入静态字段 _runtimeStats，进程存活期间不释放。
        StartRuntimeStats();
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
