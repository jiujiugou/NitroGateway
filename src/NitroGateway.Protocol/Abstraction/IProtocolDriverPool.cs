using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Protocols;

namespace NitroGateway.Protocols;

/// <summary>
/// 协议驱动连接池：按设备复用长连接驱动实例，避免每轮采集反复建连/断开。
/// 设备连接参数变化时自动重建；设备更新/删除/状态变更由上层调用 <see cref="Evict"/> 释放连接。
/// </summary>
public interface IProtocolDriverPool : IDisposable, IAsyncDisposable
{
    /// <summary>获取设备的驱动实例；连接参数未变化时复用缓存的长连接</summary>
    IProtocolDriver GetOrCreate(Device device);

    /// <summary>设备变更/删除/下线后驱逐缓存驱动，同步释放（不排水，ADR-077）</summary>
    void Evict(Guid deviceId);

    /// <summary>设备变更/删除/下线后驱逐缓存驱动，优雅异步释放（等驱动闸门）</summary>
    ValueTask EvictAsync(Guid deviceId)
    {
        Evict(deviceId);
        return ValueTask.CompletedTask;
    }

    /// <summary>默认异步释放：未显式实现的替身退化为同步 <see cref="Dispose"/></summary>
    ValueTask IAsyncDisposable.DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
