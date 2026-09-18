using NitroGateway.Domain.Devices;
using NitroGateway.Shared;
using NitroGateway.Storage.Configuration;

namespace NitroGateway.DeviceManagement;

public interface IDeviceSnapshotCache
{
    /// <summary>返回设备全量（含点位）；缓存未命中/失效/超 TTL 时从仓储加载。返回对象不得被调用方修改。</summary>
    Task<OperationResult<IReadOnlyList<Device>>> GetAllAsync(CancellationToken ct = default);

    /// <summary>配置写入后调用，使缓存立即失效，下一轮读取加载最新配置。</summary>
    void Invalidate();
}
