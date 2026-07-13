using System.Diagnostics;
using NitroGateway.Domain.Protocols;
using NitroGateway.Shared;
using DomainDevice = NitroGateway.Domain.Devices.Device;
using NitroGateway.Protocols;
using NitroGateway.Telemetry.Tracing;
using Microsoft.Extensions.Logging;

namespace NitroGateway.Collection;

/// <summary>从设备读取原始数据实现</summary>
public sealed class DeviceReader : IDeviceReader
{
    private readonly IProtocolDriverFactory _driverFactory;
    private readonly ILogger<DeviceReader> _logger;

    public DeviceReader(IProtocolDriverFactory driverFactory, ILogger<DeviceReader> logger)
    {
        _driverFactory = driverFactory;
        _logger = logger;
    }

    public async Task<OperationResult<IReadOnlyList<RawPointValue>>> ReadDeviceAsync(
        DomainDevice device, CancellationToken ct)
    {
        using var activity = GatewayActivitySource.Source.StartActivity(GatewayActivities.ReadDevice);
        activity?.SetTag(GatewayActivityTags.DeviceId, device.Id.ToString());
        activity?.SetTag(GatewayActivityTags.DeviceProtocol, device.Protocol.Name);

        _logger.LogDebug("开始读取设备：{device}", device.Name);
        var points = device.Points.Where(p => p.Enabled).ToList();
        if (points.Count == 0)
            return Array.Empty<RawPointValue>();

        var driver = _driverFactory.Create(device.Protocol, device.Connection);

        const int maxRetry = 3;
        var delay = TimeSpan.FromMilliseconds(200);

        Exception? lastError = null;

        for (int i = 0; i < maxRetry; i++)
        {
            try
            {
                var connectResult = await driver.ConnectAsync(ct);
                if (connectResult.IsFailure)
                    throw new Exception(connectResult.Error!.Message);

                var readResult = await driver.ReadBatchAsync(points, ct);

                if (readResult.IsSuccess)
                    return readResult;

                throw new Exception(readResult.Error!.Message);
            }
            catch (Exception ex)
            {
                lastError = ex;
                _logger.LogDebug(ex,
                    "设备读取失败，第 {try}/{max} 次重试：{device}",
                    i + 1, maxRetry, device.Name);

                if (i < maxRetry - 1)
                    await Task.Delay(delay * (1 << i), ct); // 指数退避
            }
            finally
            {
                await driver.DisconnectAsync(ct);
            }
        }
        return OperationalError.Timeout(
            $"设备读取失败，{maxRetry} 次重试后仍不可达: {lastError?.Message}");
    }
}
