using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NitroGateway.Desktop.Messaging;
using NitroGateway.Desktop.Services.Dialogs;
using NitroGateway.Desktop.Services.Sync;
using NitroGateway.Desktop.ViewModels;
using NitroGateway.DeviceManagement;

namespace NitroGateway.Desktop.Services.Infrastructure;

/// <summary>
/// 设备列表 ViewModel 工厂实现：按协议分区创建 <see cref="DevicesViewModel"/>（Modbus/S7 与 OPC UA 各一份）。
/// <paramref name="scope"/> 的生命周期由调用方（MainViewModel）持有并随窗口关闭释放。
/// </summary>
public sealed class DevicesViewModelFactory : IDevicesViewModelFactory
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDeviceSnapshotCache _cache;
    private readonly IDeviceHealthMonitor _health;
    private readonly UiDispatcher _ui;
    private readonly EventBridge _bridge;
    private readonly ILogger<DevicesViewModel> _logger;

    public DevicesViewModelFactory(
        IServiceScopeFactory scopeFactory,
        IDeviceSnapshotCache cache,
        IDeviceHealthMonitor health,
        UiDispatcher ui,
        EventBridge bridge,
        ILogger<DevicesViewModel> logger)
    {
        _scopeFactory = scopeFactory;
        _cache = cache;
        _health = health;
        _ui = ui;
        _bridge = bridge;
        _logger = logger;
    }

    public DevicesViewModel Create(DeviceListScope scope)
    {
        // 对话框/outbox 从 scope 解析，避免与 DeviceDialogService 构造期循环依赖（两者均为单例）
        using var serviceScope = _scopeFactory.CreateScope();
        var dialogs = serviceScope.ServiceProvider.GetRequiredService<IDeviceDialogService>();
        var outbox = serviceScope.ServiceProvider.GetRequiredService<IConfigSyncOutboxStore>();
        return new DevicesViewModel(
            _cache, _health, _ui, _bridge, _logger, _scopeFactory, dialogs, outbox,
            timer: null, scope: scope);
    }
}
