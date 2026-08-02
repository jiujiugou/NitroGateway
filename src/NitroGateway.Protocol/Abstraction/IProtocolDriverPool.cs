using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Protocols;

namespace NitroGateway.Protocols;

/// <summary>
/// 协议驱动连接池：按设备复用长连接驱动实例，避免每轮采集反复建连/断开。
/// 设备连接参数变化时自动重建；设备更新/删除/状态变更由上层调用 <see cref="Evict"/> 释放连接。
/// </summary>
public interface IProtocolDriverPool : IDisposable
{
    /// <summary>获取设备的驱动实例；连接参数未变化时复用缓存的长连接</summary>
    IProtocolDriver GetOrCreate(Device device);

    /// <summary>设备变更/删除/下线后驱逐缓存驱动，释放其底层连接</summary>
    void Evict(Guid deviceId);
}
