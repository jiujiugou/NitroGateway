using NitroGateway.Domain.Protocols;
using NitroGateway.Domain.Devices;
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
    /// 纯查询：返回该设备本轮"到期"的启用点位（距上次采集已达各自采样间隔），用于提前跳过未到期设备。
    /// 不产生副作用（不更新"上次采集时间"，该更新只在 <see cref="ReadDeviceAsync"/> 实际读取时发生）。
    /// </summary>
    /// <param name="device">目标设备（含点位列表）</param>
    /// <returns>
    /// 无启用点位返回 <c>null</c>；有启用点位但均未到期返回空列表；否则返回到期点位集合。
    /// 点位采样间隔 <c>ScanIntervalMs &lt;= 0</c> 时继承全局采集间隔（负数已在配置/UI 层拦截）。
    /// </returns>
    IReadOnlyList<DevicePoint>? GetDuePoints(DomainDevice device);

    /// <summary>
    /// 对单台设备执行一轮读取，返回原始值列表。
    /// </summary>
    /// <param name="device">目标设备（含协议、连接参数、点位列表）</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>成功返回原始点位值；设备无启用点位时返回空列表；失败返回 OperationResult 错误</returns>
    Task<OperationResult<IReadOnlyList<RawPointValue>>> ReadDeviceAsync(
        DomainDevice device, CancellationToken ct);
}
