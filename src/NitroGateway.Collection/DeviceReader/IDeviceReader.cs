using NitroGateway.Domain.Protocols;
using NitroGateway.Shared;
using DomainDevice = NitroGateway.Domain.Devices.Device;

namespace NitroGateway.Collection;

/// <summary>
/// 设备数据读取器。从设备读取一轮原始数据（协议解码后但未经工程缩放的值）。
/// 具体驱动复用/断线恢复由 Protocol 模块的 <c>IProtocolDriverPool</c> 与 <c>ReliableProtocolDriver</c> 负责。
/// </summary>
public interface IDeviceReader
{
    /// <summary>
    /// 对单台设备执行一轮读取，返回原始值列表。
    /// </summary>
    /// <param name="device">目标设备（含协议、连接参数、点位列表）</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>成功返回原始点位值；设备无启用点位时返回空列表；失败返回 OperationResult 错误</returns>
    Task<OperationResult<IReadOnlyList<RawPointValue>>> ReadDeviceAsync(
        DomainDevice device, CancellationToken ct);
}
