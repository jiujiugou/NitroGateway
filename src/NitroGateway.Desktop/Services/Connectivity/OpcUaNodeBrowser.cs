using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NitroGateway.DeviceManagement;
using NitroGateway.Domain.Protocols;
using NitroGateway.Protocols;
using NitroGateway.Shared;

namespace NitroGateway.Desktop.Services.Connectivity;

/// <inheritdoc cref="IOpcUaNodeBrowser" />
public sealed class OpcUaNodeBrowser : IOpcUaNodeBrowser
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IProtocolDriverPool _pool;
    private readonly ILogger<OpcUaNodeBrowser> _logger;

    public OpcUaNodeBrowser(
        IServiceScopeFactory scopeFactory,
        IProtocolDriverPool pool,
        ILogger<OpcUaNodeBrowser> logger)
    {
        _scopeFactory = scopeFactory;
        _pool = pool;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<OperationResult<IReadOnlyList<BrowseNode>>> BrowseAsync(
        Guid deviceId, string parentNodeId, CancellationToken ct = default)
    {
        // IDeviceManager 为 Scoped、驱动池为 Singleton：本服务按单例使用，故设备仓储每次调用从 scope 解析
        using var scope = _scopeFactory.CreateScope();
        var devices = scope.ServiceProvider.GetRequiredService<IDeviceManager>();

        var deviceResult = await devices.GetAsync(deviceId, ct);
        if (deviceResult.IsFailure)
            return OperationResult<IReadOnlyList<BrowseNode>>.Failure(deviceResult.Error!);

        try
        {
            var driver = _pool.GetOrCreate(deviceResult.Value!);
            // 能力声明优先：非 OPC UA（Modbus/S7）不建连即拒绝，避免为浏览空连一次
            if (!driver.Capability.SupportsBrowse || driver is not IBrowseableDriver browseable)
                return OperationalError.Validation("协议不支持节点浏览");

            if (driver.State != DriverState.Connected)
            {
                var connect = await driver.ConnectAsync(ct);
                if (connect.IsFailure)
                    return connect.Error!;
            }

            return await browseable.BrowseAsync(parentNodeId ?? "", ct);
        }
        catch (Exception ex)
        {
            // 驱动已释放/异常等归类返回，避免冒泡到 UI 线程
            _logger.LogWarning(ex, "OPC UA 节点浏览失败 device={DeviceId} parent={Parent}", deviceId, parentNodeId);
            return OperationalError.General($"浏览异常：{ex.Message}");
        }
    }
}
