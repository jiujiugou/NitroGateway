using NitroGateway.Domain.Protocols;
using NitroGateway.Shared;
using DomainDevice = NitroGateway.Domain.Devices.Device;

namespace NitroGateway.Collection;

/// <summary>从设备读取原始数据</summary>
public interface IDeviceReader
{
    /// <summary>对单台设备执行一轮采集，返回原始值列表</summary>
    Task<OperationResult<IReadOnlyList<RawPointValue>>> ReadDeviceAsync(
        DomainDevice device, CancellationToken ct);
}
