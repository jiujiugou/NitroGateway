using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Protocols;
using NitroGateway.Protocols;

namespace NitroGateway.CoyoteTests;

/// <summary>
/// ProtocolDriverPool 不变量 I1–I5 的 Coyote 检测器（对应 notes/Invariants/protocol-driver-pool.md）。
/// 每个正例对真实实现断言不变量必须恒成立；每个负控对"故意写坏的实现"断言调度器必须能找到反例。
/// </summary>
internal static class ProtocolDriverPoolInvariants
{
    private const int Parallelism = 8;

    // ── I1：同设备同 key 并发 GetOrCreate 只建一次且同实例 ──

    public static Task I1_Positive()
    {
        var factory = new CountingFactory();
        return RunI1(new ProtocolDriverPool(factory), factory, TestData.MakeDevice());
    }

    /// <summary>负控：完全无锁的坏池，期望被抓。</summary>
    public static Task I1_Negative_Unlocked()
    {
        var factory = new CountingFactory();
        return RunI1(new UnlockedPool(factory), factory, TestData.MakeDevice());
    }

    /// <summary>负控：使用 <see cref="Lock"/> 但把 Create 放到临界区外——用于验证 Lock 不阻碍检测。</summary>
    public static Task I1_Negative_LockedCheckThenAct()
    {
        var factory = new CountingFactory();
        return RunI1(new LockedCheckThenActPool(factory), factory, TestData.MakeDevice());
    }

    private static async Task RunI1(IProtocolDriverPool pool, CountingFactory factory, Device device)
    {
        var tasks = Enumerable.Range(0, Parallelism)
            .Select(_ => Task.Run(() => pool.GetOrCreate(device)))
            .ToArray();

        await Task.WhenAll(tasks);

        if (factory.CreatedCount != 1)
            throw new InvalidOperationException($"I1: 并发只应创建一次，实际 CreatedCount={factory.CreatedCount}");

        var first = tasks[0].Result;
        if (tasks.Any(t => !ReferenceEquals(t.Result, first)))
            throw new InvalidOperationException("I1: 应复用同一实例");
    }

    // ── I6：释放不在临界区内（阻塞式释放时其他设备仍可获取）──

    public static Task I6_Positive()
    {
        var factory = new BlockingDisposeFactory();
        return RunI6(new ProtocolDriverPool(factory), factory);
    }

    /// <summary>负控：在锁内释放驱动的坏池，阻塞式 Dispose 会冻结全池 → 期望以死锁被抓。</summary>
    public static Task I6_Negative_DisposeInLock()
    {
        var factory = new BlockingDisposeFactory();
        return RunI6(new DisposeInLockPool(factory), factory);
    }

    private static async Task RunI6(IProtocolDriverPool pool, BlockingDisposeFactory factory)
    {
        var a = TestData.MakeDevice();
        var b = TestData.MakeDevice();

        pool.GetOrCreate(a);
        var driverA = factory.Last;

        var evict = Task.Run(() => pool.Evict(a.Id));
        await driverA.Entered;                     // A 的释放已进入（阻塞在 Dispose）

        // 关键：A 的释放发生在锁外 → 获取 B 不应被阻塞
        var driverB = pool.GetOrCreate(b);
        if (driverB is null)
            throw new InvalidOperationException("I6: A 释放进行中应仍能获取 B 的驱动");

        driverA.Release();
        await evict;
    }

    // ── I2/I3：每设备至多一条目 / 每个驱动恰释放一次（账平）──
    public static Task I3_Positive()
    {
        var factory = new CountingFactory();
        var devices = Enumerable.Range(0, 4).Select(_ => TestData.MakeDevice()).ToArray();
        return RunAccountBalance(new ProtocolDriverPool(factory), factory, devices);
    }

    /// <summary>负控：Evict 只移除不释放（泄漏），期望被抓。</summary>
    public static Task I3_Negative_LeakyEvict()
    {
        var factory = new CountingFactory();
        var devices = Enumerable.Range(0, 4).Select(_ => TestData.MakeDevice()).ToArray();
        return RunAccountBalance(new LeakyEvictPool(factory), factory, devices);
    }

    private static async Task RunAccountBalance(IProtocolDriverPool pool, CountingFactory factory, Device[] devices)
    {
        var tasks = new List<Task>();
        foreach (var device in devices)
        {
            var local = device;
            tasks.Add(Task.Run(() => pool.GetOrCreate(local)));
            tasks.Add(Task.Run(() => pool.GetOrCreate(local)));
        }
        await Task.WhenAll(tasks);

        // 换连接参数：触发重建 + 锁外释放旧驱动
        foreach (var device in devices)
        {
            var local = device;
            tasks.Add(Task.Run(() =>
            {
                local.Connection = new DeviceConnection { Endpoint = "10.0.0.9:502" };
                pool.GetOrCreate(local);
            }));
        }
        await Task.WhenAll(tasks);

        // 驱逐一半
        foreach (var device in devices.Take(devices.Length / 2))
        {
            var local = device;
            tasks.Add(Task.Run(() => pool.Evict(local.Id)));
        }
        await Task.WhenAll(tasks);

        pool.Dispose();

        if (factory.CreatedCount != factory.TotalReleases)
            throw new InvalidOperationException($"I3: 账不平 创建={factory.CreatedCount} 释放={factory.TotalReleases}");

        foreach (var driver in factory.Drivers)
        {
            if (driver.ReleaseCount != 1)
                throw new InvalidOperationException($"I3: 某驱动释放次数={driver.ReleaseCount}（应恰为 1）");
        }
    }

    // ── I4：Dispose 幂等 + 销毁后 GetOrCreate 抛 ODE / Evict no-op ──

    public static Task I4_Positive()
    {
        var factory = new CountingFactory();
        var devices = Enumerable.Range(0, 3).Select(_ => TestData.MakeDevice()).ToArray();
        return RunDisposeIdempotency(new ProtocolDriverPool(factory), factory, devices);
    }

    /// <summary>负控：无幂等守卫的 Dispose，并发释放应重复释放，期望被抓。</summary>
    public static Task I4_Negative_NonIdempotentDispose()
    {
        var factory = new CountingFactory();
        var devices = Enumerable.Range(0, 3).Select(_ => TestData.MakeDevice()).ToArray();
        return RunDisposeIdempotency(new NonIdempotentDisposePool(factory), factory, devices);
    }

    private static async Task RunDisposeIdempotency(IProtocolDriverPool pool, CountingFactory factory, Device[] devices)
    {
        var created = devices.Select(d => Task.Run(() => pool.GetOrCreate(d))).ToArray();
        await Task.WhenAll(created);

        var disposals = Enumerable.Range(0, 4).Select(_ => Task.Run(pool.Dispose)).ToArray();
        await Task.WhenAll(disposals);

        foreach (var driver in factory.Drivers)
        {
            if (driver.ReleaseCount != 1)
                throw new InvalidOperationException($"I4: 某驱动释放次数={driver.ReleaseCount}（应恰为 1）");
        }

        var threw = false;
        try { pool.GetOrCreate(devices[0]); }
        catch (ObjectDisposedException) { threw = true; }
        if (!threw)
            throw new InvalidOperationException("I4: 销毁后 GetOrCreate 应抛 ObjectDisposedException");

        var before = factory.TotalReleases;
        pool.Evict(devices[0].Id);
        if (factory.TotalReleases != before)
            throw new InvalidOperationException("I4: 销毁后 Evict 应为 no-op");
    }

    // ── I5：Create 抛异常不泄漏、无半装条目；换键失败保留旧条目 ──

    public static Task I5_Positive()
    {
        var factory = new CountingFactory();
        var pool = new ProtocolDriverPool(factory);
        var device = TestData.MakeDevice();
        var original = device.Connection;

        var oldDriver = pool.GetOrCreate(device);
        if (factory.CreatedCount != 1)
            throw new InvalidOperationException("I5: 前置创建应恰好一次");

        // 换 key 且工厂抛异常：异常应传播，失败创建不记账
        factory.ThrowOnCreate = true;
        device.Connection = new DeviceConnection { Endpoint = "10.0.0.9:502" };

        var threw = false;
        try { pool.GetOrCreate(device); }
        catch (InvalidOperationException) { threw = true; }
        if (!threw)
            throw new InvalidOperationException("I5: Create 异常应向外传播");
        if (factory.CreatedCount != 1)
            throw new InvalidOperationException($"I5: 失败创建不应记账/泄漏，CreatedCount={factory.CreatedCount}");

        // 恢复工厂与原 key：旧条目应仍可复用，且未被释放
        factory.ThrowOnCreate = false;
        device.Connection = original;
        var same = pool.GetOrCreate(device);

        if (!ReferenceEquals(same, oldDriver))
            throw new InvalidOperationException("I5: 换键失败应保留旧条目");
        if (factory.CreatedCount != 1)
            throw new InvalidOperationException("I5: 复用旧条目不应新建");
        if (factory.Drivers.Single().ReleaseCount != 0)
            throw new InvalidOperationException("I5: 旧驱动不应被释放");

        return Task.CompletedTask;
    }

    // ══════════════ 坏实现（negative-control 种子） ══════════════

    /// <summary>无锁坏池：check-then-act 完全暴露。</summary>
    private sealed class UnlockedPool(IProtocolDriverFactory factory) : IProtocolDriverPool
    {
        private readonly Dictionary<Guid, IProtocolDriver> _drivers = new();

        public IProtocolDriver GetOrCreate(Device device)
        {
            if (_drivers.TryGetValue(device.Id, out var existing))
                return existing;
            var created = factory.Create(device.Protocol, device.Connection);
            _drivers[device.Id] = created;
            return created;
        }

        public void Evict(Guid deviceId) => _drivers.Remove(deviceId);

        public void Dispose()
        {
            foreach (var driver in _drivers.Values) driver.Dispose();
            _drivers.Clear();
        }
    }

    /// <summary>持锁但临界区过小的坏池：Create 在锁外（用于验证 Lock 是否被调度）。</summary>
    private sealed class LockedCheckThenActPool(IProtocolDriverFactory factory) : IProtocolDriverPool
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<Guid, IProtocolDriver> _drivers = new();

        public IProtocolDriver GetOrCreate(Device device)
        {
            lock (_gate)
            {
                if (_drivers.TryGetValue(device.Id, out var existing))
                    return existing;
            }

            var created = factory.Create(device.Protocol, device.Connection);   // 坏：在锁外创建

            lock (_gate)
            {
                _drivers[device.Id] = created;
            }
            return created;
        }

        public void Evict(Guid deviceId)
        {
            lock (_gate) _drivers.Remove(deviceId);
        }

        public void Dispose()
        {
            lock (_gate)
            {
                foreach (var driver in _drivers.Values) driver.Dispose();
                _drivers.Clear();
            }
        }
    }

    /// <summary>Evict 只移除不释放的坏池（泄漏）。</summary>
    private sealed class LeakyEvictPool(IProtocolDriverFactory factory) : IProtocolDriverPool
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<Guid, IProtocolDriver> _drivers = new();

        public IProtocolDriver GetOrCreate(Device device)
        {
            lock (_gate)
            {
                if (_drivers.TryGetValue(device.Id, out var existing))
                    return existing;
                var created = factory.Create(device.Protocol, device.Connection);
                _drivers[device.Id] = created;
                return created;
            }
        }

        public void Evict(Guid deviceId)
        {
            lock (_gate) _drivers.Remove(deviceId);   // 坏：不释放
        }

        public void Dispose()
        {
            lock (_gate)
            {
                foreach (var driver in _drivers.Values) driver.Dispose();
                _drivers.Clear();
            }
        }
    }

    /// <summary>无幂等守卫的坏池：并发 Dispose 会重复释放。</summary>
    private sealed class NonIdempotentDisposePool(IProtocolDriverFactory factory) : IProtocolDriverPool
    {
        private readonly Dictionary<Guid, IProtocolDriver> _drivers = new();

        public IProtocolDriver GetOrCreate(Device device)
        {
            if (_drivers.TryGetValue(device.Id, out var existing))
                return existing;
            var created = factory.Create(device.Protocol, device.Connection);
            _drivers[device.Id] = created;
            return created;
        }

        public void Evict(Guid deviceId) => _drivers.Remove(deviceId);

        public void Dispose()
        {
            // 坏：无 Interlocked 幂等守卫，并发调用重复释放
            foreach (var driver in _drivers.Values) driver.Dispose();
            _drivers.Clear();
        }
    }

    /// <summary>在锁内释放驱动的坏池（I6 负控）：慢释放会阻塞全池。</summary>
    private sealed class DisposeInLockPool(IProtocolDriverFactory factory) : IProtocolDriverPool
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<Guid, IProtocolDriver> _drivers = new();

        public IProtocolDriver GetOrCreate(Device device)
        {
            lock (_gate)
            {
                if (_drivers.TryGetValue(device.Id, out var existing))
                    return existing;
                var created = factory.Create(device.Protocol, device.Connection);
                _drivers[device.Id] = created;
                return created;
            }
        }

        public void Evict(Guid deviceId)
        {
            lock (_gate)
            {
                if (_drivers.Remove(deviceId, out var entry))
                    entry.Dispose();   // 坏：在临界区内释放
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                foreach (var driver in _drivers.Values) driver.Dispose();
                _drivers.Clear();
            }
        }
    }
}
