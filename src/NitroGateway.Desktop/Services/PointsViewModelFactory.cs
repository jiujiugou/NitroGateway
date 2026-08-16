using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NitroGateway.Desktop.ViewModels;
using NitroGateway.DeviceManagement;

namespace NitroGateway.Desktop.Services;

/// <summary>
/// 点位 ViewModel 工厂实现：scope 内解析 ICsvFileService / PointBatchService / 对话框，
/// 调用方无需逐个 GetRequiredService 追依赖来源。
/// 依赖均为 Singleton（CsvFileService、PointBatchService、DeviceDialogService），
/// 故 scope 在 Create 返回即释放无副作用；若未来改为 Scoped 需把 scope 生命周期上提到调用方。
/// </summary>
public sealed class PointsViewModelFactory : IPointsViewModelFactory
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfigSyncOutboxStore _outbox;
    private readonly ILogger<PointsViewModel> _logger;

    public PointsViewModelFactory(
        IServiceScopeFactory scopeFactory,
        IConfigSyncOutboxStore outbox,
        ILogger<PointsViewModel> logger)
    {
        _scopeFactory = scopeFactory;
        _outbox = outbox;
        _logger = logger;
    }

    public PointsViewModel Create(Guid deviceId, string deviceName)
    {
        // 对话框从 scope 解析避免与 DeviceDialogService 构造期循环依赖（两者均为单例）
        using var scope = _scopeFactory.CreateScope();
        var dialogs = scope.ServiceProvider.GetRequiredService<IDeviceDialogService>();
        var csvFiles = scope.ServiceProvider.GetRequiredService<ICsvFileService>();
        var batch = scope.ServiceProvider.GetRequiredService<PointBatchService>();
        return new PointsViewModel(deviceId, deviceName, _scopeFactory, dialogs, _outbox, csvFiles, batch, _logger);
    }
}
