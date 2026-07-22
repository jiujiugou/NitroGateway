using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NitroGateway.DeviceManagement.Events;

namespace NitroGateway.DeviceManagement.Listeners;

/// <summary>持久化监听器：HealthMonitor 状态变更 → 写数据库</summary>
public sealed class PersistenceListener : IDeviceHealthListener
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PersistenceListener> _logger;

    public PersistenceListener(IServiceScopeFactory scopeFactory, ILogger<PersistenceListener> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async ValueTask OnHealthChangedAsync(DeviceHealthChanged e, CancellationToken ct = default)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var manager = scope.ServiceProvider.GetRequiredService<IDeviceManager>();
            var result = await manager.UpdateStatusAsync(e.DeviceId, e.NewStatus, ct);
            if (result.IsFailure)
                _logger.LogError("设备状态持久化失败: {DeviceId} → {Status}: {Error}", e.DeviceId, e.NewStatus, result.Error!.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "设备状态持久化异常: {DeviceId} → {Status}", e.DeviceId, e.NewStatus);
        }
    }
}
