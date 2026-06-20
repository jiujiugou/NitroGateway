using NitroGateway.Domain.Protocols;
using NitroGateway.Shared;
using DomainDevice = NitroGateway.Domain.Devices.Device;
using NitroGateway.Protocol;

namespace NitroGateway.Collection;

/// <summary>从设备读取原始数据实现</summary>
public sealed class DeviceReader : IDeviceReader
{
    private readonly IProtocolDriverFactory _driverFactory;

    public DeviceReader(IProtocolDriverFactory driverFactory)
    {
        _driverFactory = driverFactory;
    }

    public async Task<OperationResult<IReadOnlyList<RawPointValue>>> ReadDeviceAsync(
        DomainDevice device, CancellationToken ct)
    {
        var points = device.Points.Where(p => p.Enabled).ToList();
        if (points.Count == 0)
            return Array.Empty<RawPointValue>();

        var driver = _driverFactory.Create(device.Protocol, device.Connection);

        var connectResult = await driver.ConnectAsync(ct);
        if (connectResult.IsFailure)
            return connectResult.Error!;

        try
        {
            var readResult = await driver.ReadBatchAsync(points, ct);
            return readResult.IsSuccess
                ? OperationResult<IReadOnlyList<RawPointValue>>.Success(readResult.Value!)
                : readResult.Error!;
        }
        finally
        {
            await driver.DisconnectAsync(ct);
        }
    }
}
