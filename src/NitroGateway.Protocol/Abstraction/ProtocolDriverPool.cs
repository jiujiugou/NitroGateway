using System.Text.Json;
using System.Threading;
using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Protocols;

namespace NitroGateway.Protocols
{
    /// <summary>
    /// 协议驱动连接池实现。
    /// 以设备 ID + 连接参数指纹为键：参数不变则复用长连接，参数变化自动重建并驱逐旧驱动。
    /// <para><b>并发模型：</b>单个 <see cref="Lock"/> 保护「查找-创建-安装」这一复合操作，
    /// 保证任意交错下不重复创建、不泄漏；驱动的创建与释放（可能含 I/O 的慢动作）放在锁外执行。</para>
    /// <para><b>已知边界：</b>本池只保证「字典状态一致」，不保证「正在被调用方使用的驱动不被释放」。
    /// <see cref="Evict"/> / <see cref="Dispose"/> 与在途读取之间仍有 use-after-dispose 窗口，
    /// 靠「仅在设备离线/变更时驱逐」的上层约定规避（详见并发测试的已知限制用例）。</para>
    /// </summary>
    public sealed class ProtocolDriverPool : IProtocolDriverPool
    {
        private readonly IProtocolDriverFactory _factory;
        /// <summary>保护 <see cref="_drivers"/> 的锁；保护范围只含字典读写，不含驱动创建/释放。</summary>
        private readonly Lock _gate = new();
        private readonly Dictionary<Guid, Entry> _drivers = new();
        /// <summary>0=存活，1=已销毁；用 <see cref="Interlocked"/> 保证 <see cref="Dispose"/> 幂等。</summary>
        private int _disposed;

        private sealed record Entry(string Key, IProtocolDriver Driver);

        public ProtocolDriverPool(IProtocolDriverFactory factory) => _factory = factory;

        public IProtocolDriver GetOrCreate(Device device)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

            var key = BuildKey(device);
            IProtocolDriver created;
            IProtocolDriver? stale = null;

            lock (_gate)
            {
                // 双检：等待锁期间池可能已被 Dispose（Dispose 取同一把锁）
                ObjectDisposedException.ThrowIf(_disposed != 0, this);

                if (_drivers.TryGetValue(device.Id, out var existing))
                {
                    if (existing.Key == key)
                        return existing.Driver;   // 连接参数未变，复用长连接

                    stale = existing.Driver;       // 参数已变，旧驱动待释放
                }

                created = _factory.Create(device.Protocol, device.Connection);
                _drivers[device.Id] = new Entry(key, created);
            }

            stale?.Dispose();                      // 锁外释放，避免慢 I/O 阻塞其他设备
            return created;
        }

        public void Evict(Guid deviceId)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;                            // 池已销毁：Dispose 已清理全部，无需再驱逐

            IProtocolDriver? stale = null;
            lock (_gate)
            {
                if (_drivers.Remove(deviceId, out var entry))
                    stale = entry.Driver;
            }

            stale?.Dispose();                      // 锁外释放
        }

        public void Dispose()
        {
            // 幂等：仅首个调用者执行释放，重复调用直接返回
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            IProtocolDriver[] all;
            lock (_gate)
            {
                all = new IProtocolDriver[_drivers.Count];
                var i = 0;
                foreach (var entry in _drivers.Values)
                    all[i++] = entry.Driver;
                _drivers.Clear();
            }

            foreach (var driver in all)
            {
                // 单个驱动释放异常不阻断其余释放（Dispose 不应向外抛）
                try { driver.Dispose(); } catch { }
            }
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
}
