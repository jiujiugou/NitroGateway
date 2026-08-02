using System.Collections.Concurrent;
using System.Text.Json;
using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Protocols;

namespace NitroGateway.Protocols;

/// <summary>
/// 协议驱动连接池实现。
/// 以设备 ID + 连接参数指纹为键：参数不变则复用长连接，参数变化自动重建并驱逐旧驱动。
/// 并发创建同一设备驱动时保证只有一个存活，其余立即释放。
/// </summary>
public sealed class ProtocolDriverPool : IProtocolDriverPool
{
    private readonly IProtocolDriverFactory _factory;
    private readonly ConcurrentDictionary<Guid, Entry> _drivers = new();

    private sealed record Entry(string Key, IProtocolDriver Driver);

    public ProtocolDriverPool(IProtocolDriverFactory factory) => _factory = factory;

    public IProtocolDriver GetOrCreate(Device device)
    {
        var key = BuildKey(device);

        // 快速路径：缓存命中且连接参数未变化
        if (_drivers.TryGetValue(device.Id, out var existing) && existing.Key == key)
            return existing.Driver;

        var driver = _factory.Create(device.Protocol, device.Connection);
        var entry = new Entry(key, driver);

        while (true)
        {
            var winner = _drivers.GetOrAdd(device.Id, entry);
            if (ReferenceEquals(winner, entry))
                return driver;

            if (winner.Key == key)
            {
                // 并发创建了同配置驱动：复用已存在者，释放本次新建
                try { driver.Dispose(); } catch { }
                return winner.Driver;
            }

            // 池中仍是旧配置：替换并释放旧驱动
            if (_drivers.TryUpdate(device.Id, entry, winner))
            {
                try { winner.Driver.Dispose(); } catch { }
                return driver;
            }
            // 被其他线程抢先更新，重试
        }
    }

    public void Evict(Guid deviceId)
    {
        if (_drivers.TryRemove(deviceId, out var entry))
        {
            try { entry.Driver.Dispose(); } catch { }
        }
    }

    public void Dispose()
    {
        foreach (var entry in _drivers.Values)
        {
            try { entry.Driver.Dispose(); } catch { }
        }
        _drivers.Clear();
    }

    /// <summary>连接指纹：协议 + 端点 + 超时/重试策略 + 协议参数</summary>
    private static string BuildKey(Device device)
    {
        var paramsJson = JsonSerializer.Serialize(
            device.Connection.Parameters
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .ToDictionary(kv => kv.Key, kv => kv.Value));
        return string.Join("|",
            device.Protocol.Name,
            device.Protocol.Dialect,
            device.Connection.Endpoint,
            device.Connection.ConnectTimeoutMs,
            device.Connection.RequestTimeoutMs,
            device.Connection.RetryCount,
            device.Connection.RetryIntervalMs,
            paramsJson);
    }
}
