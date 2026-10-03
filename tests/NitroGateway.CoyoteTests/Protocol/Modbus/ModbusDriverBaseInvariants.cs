using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Protocols;
using NitroGateway.Protocols.Modbus;
using NitroGateway.Shared;

namespace NitroGateway.CoyoteTests;

/// <summary>
/// ModbusDriverBase 模板不变量（对应 notes/Invariants/modbus.md I1/I6）：
/// I1：同一驱动实例的受保护操作（Connect/Disconnect/Read/Write/Ping/批量读）经<b>同一把</b> Gate 串行（ADR-074）。
/// I6：Base 模板收口的幂等位——同步/异步释放共用，并发/跨接口释放只拆一次（ADR-078）。
/// <para><b>覆盖边界：</b>本文件只验证基类模板。<c>ModbusTcpDriver</c> 的读串行/X1 异步释放见
/// <see cref="ModbusTcpDriverInvariants"/>，<c>ModbusRtuDriver</c> 的租约账目/X2 见
/// <see cref="ModbusRtuDriverInvariants"/>。仍为非目标：TCP 建连（ConnectServerAsync 非 virtual、无缝）、
/// RTU 帧级 Station 一致（I2）与换租约撕裂（I4/X3，需抽象 ModbusRtu）、S7/Mitsubishi（无 SDK 测试缝）；
/// OpcUa 的会话自愈并发见 <see cref="OpcUaDriverInvariants"/>，真实会话闸门串行仍为非目标。</para>
/// </summary>
internal static class ModbusDriverBaseInvariants
{
    private const int Parallelism = 8;

    private static DevicePoint Point => new()
    {
        Id = Guid.NewGuid(),
        Name = "P",
        Address = "40001",
        DataType = DataType.Int16
    };

    // ── I1：所有受保护操作共用同一闸门，串行进入 ──

    public static Task I1_GateSerialization_Positive()
    {
        var probe = new RendezvousProbe();
        return RunGateSerialization(new ProbeModbusDriver(probe), probe);
    }

    /// <summary>负控：完全无闸门的坏驱动（独立实现），受保护体应被并发进入 → 期望被抓。</summary>
    public static Task I1_GateSerialization_Negative_NoGate()
    {
        var probe = new RendezvousProbe();
        return RunGateSerialization(new UnguardedDriver(probe), probe);
    }

    /// <summary>负控：仅 Ping 绕过基类 GuardedAsync 的坏子类（真实回归场景）→ 期望被抓。</summary>
    public static Task I1_GateSerialization_Negative_BypassPing()
    {
        var probe = new RendezvousProbe();
        return RunGateSerialization(new BypassPingDriver(probe), probe);
    }

    private static async Task RunGateSerialization(IProtocolDriver driver, RendezvousProbe probe)
    {
        // 混合所有公开入口，验证它们落到同一把闸门；每次操作内部会 yield 制造交错机会。
        var ops = new Func<Task>[]
        {
            () => driver.ConnectAsync(),
            () => driver.DisconnectAsync(),
            () => driver.ReadAsync(Point),
            () => driver.ReadBatchAsync([Point]),
            () => driver.WriteAsync(Point, (short)1),
            () => driver.PingAsync(),
        };

        var tasks = Enumerable.Range(0, Parallelism)
            .Select(i => Task.Run(ops[i % ops.Length]))
            .ToArray();

        await Task.WhenAll(tasks);

        if (probe.MaxConcurrency > 1)
            throw new InvalidOperationException(
                $"I1: 受保护操作应串行，实际并发进入 {probe.MaxConcurrency} 个（同一 Gate 被绕过）");
    }

    // ── I6：同步/异步释放共用幂等位，核心恰拆一次 ──

    public static Task I6_DisposeIdempotent_Positive()
    {
        var driver = new CountingBaseDriver(NullLogger<CountingBaseDriver>.Instance);
        return RunDisposeIdempotent(driver, () => driver.CoreDisposeCount);
    }

    /// <summary>负控：无幂等守卫的坏驱动，并发释放重复拆除 → 期望被抓。</summary>
    public static Task I6_DisposeIdempotent_Negative_NonIdempotent()
    {
        var driver = new NonIdempotentDisposeDriver();
        return RunDisposeIdempotent(driver, () => driver.CoreDisposeCount);
    }

    private static async Task RunDisposeIdempotent(IProtocolDriver driver, Func<int> coreDisposeCount)
    {
        var disposals = Enumerable.Range(0, Parallelism)
            .Select(i => i % 2 == 0
                ? Task.Run(driver.Dispose)
                : Task.Run(async () => await driver.DisposeAsync()))
            .ToArray();

        await Task.WhenAll(disposals);

        var count = coreDisposeCount();
        if (count != 1)
            throw new InvalidOperationException($"I6: 内层核心应恰拆除一次，实际 {count}");
    }

    // ══════════════ 探针 ══════════════

    /// <summary>并发探针：进入时记录活动数峰值，退出前 yield 制造交错窗口。</summary>
    private sealed class RendezvousProbe
    {
        private int _active;
        private int _max;

        public int MaxConcurrency => Volatile.Read(ref _max);

        public async Task BodyAsync()
        {
            var active = Interlocked.Increment(ref _active);
            UpdateMax(active);
            await Task.Yield();
            Interlocked.Decrement(ref _active);
        }

        private void UpdateMax(int value)
        {
            int current;
            while (value > (current = Volatile.Read(ref _max)))
            {
                if (Interlocked.CompareExchange(ref _max, value, current) == current)
                    return;
            }
        }
    }

    // ══════════════ 测试用底座 ══════════════

    /// <summary>只提供 ModbusDriverBase 抽象成员的最小底座；受保护体经 <see cref="ProtectedBodyAsync"/> 注入。</summary>
    private abstract class FakeBaseDriver : ModbusDriverBase
    {
        private readonly SemaphoreSlim _gate = new(1, 1);

        protected FakeBaseDriver(ILogger logger) : base(logger) => State = DriverState.Connected;

        protected override SemaphoreSlim Gate => _gate;

        protected virtual Task ProtectedBodyAsync() => Task.CompletedTask;

        protected override void DisposeCore() { }

        public override Task<OperationResult> ConnectAsync(CancellationToken ct = default)
            => GuardedAsync(async _ =>
            {
                await ProtectedBodyAsync();
                State = DriverState.Connected;
                return OperationResult.Success();
            }, ct);

        public override Task<OperationResult> DisconnectAsync(CancellationToken ct = default)
            => GuardedAsync(async _ =>
            {
                await ProtectedBodyAsync();
                return OperationResult.Success();
            }, ct);

        protected override async Task<object[]?> ReadBatchTypedAsync(string address, DataType type, int count)
        {
            await ProtectedBodyAsync();
            return [(short)0];
        }

        protected override async Task<object> ReadSingleTypedAsync(DataType type, string address)
        {
            await ProtectedBodyAsync();
            return (short)0;
        }

        protected override async Task<OperationResult> WriteSingleValueAsync(
            DevicePoint point, string address, object value)
        {
            await ProtectedBodyAsync();
            return OperationResult.Success();
        }
    }

    /// <summary>正例驱动：所有受保护体都走基类 Gate，探针应恒见到并发 1。</summary>
    private sealed class ProbeModbusDriver(RendezvousProbe probe) : FakeBaseDriver(NullLogger.Instance)
    {
        protected override Task ProtectedBodyAsync() => probe.BodyAsync();
    }

    /// <summary>负控：仅 PingAsync 覆写为绕过闸门（模拟"新增方法漏加锁"）。</summary>
    private sealed class BypassPingDriver(RendezvousProbe probe) : FakeBaseDriver(NullLogger.Instance)
    {
        protected override Task ProtectedBodyAsync() => probe.BodyAsync();

        public override async Task<OperationResult> PingAsync(CancellationToken ct = default)
        {
            await ProtectedBodyAsync();   // 坏：未走 GuardedAsync
            return OperationResult.Success();
        }
    }

    /// <summary>I6 正例驱动：统计同步/异步共用的核心拆除次数。</summary>
    private sealed class CountingBaseDriver(ILogger logger) : FakeBaseDriver(logger)
    {
        private int _core;

        public int CoreDisposeCount => Volatile.Read(ref _core);

        protected override void DisposeCore() => Interlocked.Increment(ref _core);
    }

    // ══════════════ 坏实现（negative-control 种子） ══════════════

    /// <summary>完全无闸门的坏驱动：所有入口直接进入受保护体。</summary>
    private sealed class UnguardedDriver(RendezvousProbe probe) : IProtocolDriver
    {
        public DriverState State => DriverState.Connected;
        public DriverCapability Capability => new();

        public async Task<OperationResult> ConnectAsync(CancellationToken ct = default)
        {
            await probe.BodyAsync();
            return OperationResult.Success();
        }

        public async Task<OperationResult> DisconnectAsync(CancellationToken ct = default)
        {
            await probe.BodyAsync();
            return OperationResult.Success();
        }

        public async Task<OperationResult> PingAsync(CancellationToken ct = default)
        {
            await probe.BodyAsync();
            return OperationResult.Success();
        }

        public async Task<OperationResult<RawPointValue>> ReadAsync(
            DevicePoint point, CancellationToken ct = default)
        {
            await probe.BodyAsync();
            return OperationResult<RawPointValue>.Success(
                new RawPointValue { Point = point, Value = (short)0 });
        }

        public async Task<OperationResult<IReadOnlyList<RawPointValue>>> ReadBatchAsync(
            IEnumerable<DevicePoint> points, CancellationToken ct = default)
        {
            await probe.BodyAsync();
            return OperationResult<IReadOnlyList<RawPointValue>>.Success(Array.Empty<RawPointValue>());
        }

        public async Task<OperationResult> WriteAsync(
            DevicePoint point, object value, CancellationToken ct = default)
        {
            await probe.BodyAsync();
            return OperationResult.Success();
        }

        public async Task<OperationResult> WriteBatchAsync(
            IEnumerable<KeyValuePair<DevicePoint, object>> entries, CancellationToken ct = default)
        {
            await probe.BodyAsync();
            return OperationResult.Success();
        }

        public void Dispose() { }
    }

    /// <summary>无幂等守卫的坏驱动：并发同步/异步释放重复拆除内层。</summary>
    private sealed class NonIdempotentDisposeDriver : IProtocolDriver
    {
        private int _core;

        public int CoreDisposeCount => Volatile.Read(ref _core);

        public DriverState State => DriverState.Connected;
        public DriverCapability Capability => new();

        public Task<OperationResult> ConnectAsync(CancellationToken ct = default)
            => Task.FromResult(OperationResult.Success());
        public Task<OperationResult> DisconnectAsync(CancellationToken ct = default)
            => Task.FromResult(OperationResult.Success());
        public Task<OperationResult> PingAsync(CancellationToken ct = default)
            => Task.FromResult(OperationResult.Success());
        public Task<OperationResult<RawPointValue>> ReadAsync(DevicePoint point, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<OperationResult<IReadOnlyList<RawPointValue>>> ReadBatchAsync(
            IEnumerable<DevicePoint> points, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<OperationResult> WriteAsync(DevicePoint point, object value, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<OperationResult> WriteBatchAsync(
            IEnumerable<KeyValuePair<DevicePoint, object>> entries, CancellationToken ct = default)
            => throw new NotSupportedException();

        public void Dispose() => Interlocked.Increment(ref _core);   // 坏：无幂等位

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _core);   // 坏：无幂等位
            return ValueTask.CompletedTask;
        }
    }
}
