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
    /// 计算本轮应采集的到期点位（点位级 <c>ScanIntervalMs</c> 调度，ADR-062）。
    /// 纯查询、无副作用：不更新任何内部状态，也不触发驱动调用。
    /// <para><b>返回值三态：</b></para>
    /// <list type="bullet">
    /// <item><c>null</c>：设备无 enabled 点位——仍走 ADR-031 真实探活（读取仍会尝试连接）；</item>
    /// <item>空列表：有点位但全部未到采集间隔——本轮应跳过（不调驱动、不触发熔断、不更新健康快照）；</item>
    /// <item>非空：本轮到期的点位子集——仅此子集参与批量读取。</item>
    /// </list>
    /// </summary>
    /// <param name="device">目标设备（含协议、连接参数、点位列表）</param>
    /// <returns>到期点位子集；无 enabled 点位返回 <c>null</c>；全部未到期返回空列表</returns>
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
