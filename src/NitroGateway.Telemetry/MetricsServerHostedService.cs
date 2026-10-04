using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Prometheus;

namespace NitroGateway.Telemetry;

/// <summary>
/// 独立指标 HTTP 端点宿主：在无 ASP.NET 管线的进程（如 Desktop 边缘端）暴露 <c>/metrics</c>。
/// 仅当 <see cref="TelemetryMetricsOptions.Enabled"/> 为 true 时注册；启动/停止失败只记日志，不阻断宿主。
/// </summary>
internal sealed class MetricsServerHostedService : IHostedService
{
    private readonly TelemetryMetricsOptions _options;
    private readonly ILogger<MetricsServerHostedService> _logger;
    private MetricServer? _server;

    public MetricsServerHostedService(
        TelemetryMetricsOptions options, ILogger<MetricsServerHostedService> logger)
    {
        _options = options;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            _server = new MetricServer(_options.Host, _options.Port);
            _server.Start();
            _logger.LogInformation(
                "指标端点已启动: http://{Host}:{Port}/metrics", _options.Host, _options.Port);
        }
        catch (Exception ex)
        {
            // 端口占用等：记录后继续，宿主其余功能不受影响。
            _logger.LogError(ex, "指标端点启动失败（Host={Host} Port={Port}）", _options.Host, _options.Port);
            _server = null;
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            _server?.Stop();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "指标端点停止异常");
        }
        finally
        {
            _server = null;
        }

        return Task.CompletedTask;
    }
}
