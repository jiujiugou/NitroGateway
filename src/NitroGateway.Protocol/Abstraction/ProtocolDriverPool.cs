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
        /// <summary>设备 ID → 池条目（连接指纹 + 驱动实例）。所有读写都在 <see cref="_gate"/> 内进行。</summary>
        private readonly Dictionary<Guid, Entry> _drivers = new();
        /// <summary>0=存活，1=已销毁；用 <see cref="Interlocked"/> 保证 <see cref="Dispose"/> 幂等。</summary>
        private int _disposed;

        /// <summary>池条目：记录建立时的连接指纹 <paramref name="Key"/>，用于检测连接参数是否发生变化。</summary>
        private sealed record Entry(string Key, IProtocolDriver Driver);

        /// <summary>创建连接池。</summary>
        /// <param name="factory">底层驱动工厂；仅在缓存未命中或连接指纹变化时被调用。</param>
        public ProtocolDriverPool(IProtocolDriverFactory factory) => _factory = factory;

        /// <inheritdoc />
        /// <remarks>
        /// 池的核心方法，提供三条并发保证：
        /// <list type="number">
        /// <item><b>线性化</b>：同一设备并发调用时只真正创建一次驱动，其余复用同一实例
        /// （靠锁内「查找-创建-安装」这一复合操作保证）。</item>
        /// <item><b>不泄漏</b>：连接参数变化时，被替换下来的旧驱动必定被释放，且释放发生在锁外。</item>
        /// <item><b>销毁安全</b>：池已销毁时抛 <see cref="ObjectDisposedException"/>，不再安装新条目。</item>
        /// </list>
        /// <para><b>注意：</b>返回的是<b>裸借用</b>引用（ADR-077）——调用方不得自行 <c>Dispose</c>，
        /// 也不得假设使用期间该实例不会被 <see cref="Evict"/> 释放。</para>
        /// </remarks>
        public IProtocolDriver GetOrCreate(Device device)
        {
            // 快路径：池已销毁则立即失败（无锁读，避免为一个已销毁的池去争锁）
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

            // 连接指纹在锁外计算：BuildKey 是纯计算 + 序列化，不触碰共享状态
            var key = BuildKey(device);
            IProtocolDriver created;
            IProtocolDriver? stale = null;   // 参数变化时被替换下来的旧驱动，待锁外释放

            lock (_gate)
            {
                // 双检：等待锁期间池可能已被 Dispose（Dispose 取同一把锁）
                ObjectDisposedException.ThrowIf(_disposed != 0, this);

                if (_drivers.TryGetValue(device.Id, out var existing))
                {
                    if (existing.Key == key)
                        return existing.Driver;   // 连接参数未变，复用长连接（既不重建也不释放）

                    stale = existing.Driver;       // 参数已变：旧驱动待释放，稍后用新驱动覆盖该条目
                }

                // 在锁内创建并安装，使「查找-创建-安装」原子化，杜绝并发重复创建。
                // 约定 Create 只做纯构造、不建连（真正建连会在首次读写时惰性发生），否则会阻塞同池其他设备。
                created = _factory.Create(device.Protocol, device.Connection);
                _drivers[device.Id] = new Entry(key, created);
            }

            stale?.Dispose();                      // 锁外释放，避免慢 I/O 阻塞其他设备
            return created;
        }

        /// <inheritdoc />
        /// <remarks>
        /// 由设备注册 / 注销 / 软删 / 状态变更触发：移除条目并释放旧驱动。
        /// 池已销毁时为 no-op——此时 <see cref="Dispose"/> 已清理全部驱动。
        /// </remarks>
        public void Evict(Guid deviceId)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;                            // 池已销毁：Dispose 已清理全部，无需再驱逐

            IProtocolDriver? stale = null;
            lock (_gate)
            {
                // 移除条目与取出驱动引用在锁内完成，真正的释放留到锁外
                if (_drivers.Remove(deviceId, out var entry))
                    stale = entry.Driver;
            }

            stale?.Dispose();                      // 锁外释放
        }

        /// <inheritdoc />
        /// <remarks>
        /// 与 <see cref="Evict"/> 相同，但用异步释放优雅拆除驱动（可等待其闸门）。
        /// </remarks>
        public async ValueTask EvictAsync(Guid deviceId)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;                            // 池已销毁：Dispose 已清理全部，无需再驱逐

            IProtocolDriver? stale = null;
            lock (_gate)
            {
                if (_drivers.Remove(deviceId, out var entry))
                    stale = entry.Driver;
            }

            if (stale is not null)
                await stale.DisposeAsync();        // 锁外异步释放
        }

        /// <inheritdoc />
        /// <remarks>
        /// 幂等：仅首个调用者执行清理。先把全部驱动快照出字典，再在锁外逐个释放。
        /// 本方法<b>不排水</b>——不等待在途调用（ADR-077），释放后到达的调用由装饰器快速失败。
        /// </remarks>
        public void Dispose()
        {
            // 幂等：Interlocked.Exchange 原子置位，仅首个调用者继续执行，重复调用直接返回
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            IProtocolDriver[] all;
            lock (_gate)
            {
                // 快照 + 清空：把释放动作全部移到锁外，避免慢 I/O 持锁
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

        /// <inheritdoc />
        /// <remarks>
        /// 与 <see cref="Dispose"/> 相同，但逐个 <c>await</c> 异步释放驱动，便于优雅关停。
        /// </remarks>
        public async ValueTask DisposeAsync()
        {
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
                try { await driver.DisposeAsync(); } catch { }
            }
        }

        /// <summary>
        /// 连接指纹：协议 + 端点 + 超时/重试策略 + 协议参数。
        /// 键不变则复用长连接；任一字段变化即视为「换了连接」，触发重建。
        /// </summary>
        private static string BuildKey(Device device)
        {
            // 协议参数是字典：序列化前按键排序，使相同参数恒定得到相同字符串（不受字典枚举顺序影响）
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
