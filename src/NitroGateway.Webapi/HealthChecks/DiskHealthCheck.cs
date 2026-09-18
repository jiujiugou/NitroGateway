using Microsoft.Extensions.Diagnostics.HealthChecks;
using NitroGateway.Storage.Disk;

namespace NitroGateway.Webapi.HealthChecks;

public sealed class DiskHealthCheck : IHealthCheck
{
    private readonly IDiskStatus _disk;

    public DiskHealthCheck(IDiskStatus disk)
    {
        _disk = disk;
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken ct = default)
    {
        return _disk.Level switch
        {
            DiskLevel.Healthy => Task.FromResult(HealthCheckResult.Healthy("磁盘空间充足")),
            DiskLevel.Warning => Task.FromResult(HealthCheckResult.Degraded("磁盘剩余空间低于 Warning 阈值")),
            _ => Task.FromResult(HealthCheckResult.Unhealthy("磁盘剩余空间低于 Critical 阈值，写入已降级"))
        };
    }
}
