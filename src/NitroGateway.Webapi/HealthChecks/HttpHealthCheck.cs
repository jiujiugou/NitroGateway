using Microsoft.Extensions.Diagnostics.HealthChecks;
using NitroGateway.Transport.HTTP;

namespace NitroGateway.Webapi.HealthChecks;

public sealed class HttpHealthCheck : IHealthCheck
{
    private readonly IHttpClient _http;

    public HttpHealthCheck(IHttpClient http)
    {
        _http = http;
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken ct = default)
    {
        return _http.State == HttpConnectionState.Connected
            ? Task.FromResult(HealthCheckResult.Healthy("HTTP 北向通道已连接"))
            : Task.FromResult(HealthCheckResult.Degraded($"HTTP 北向通道不可用（状态 {_http.State}）"));
    }
}
