using Microsoft.Extensions.Logging.Abstractions;
using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Protocols;
using NitroGateway.Protocol.Abstractions;
using NitroGateway.Shared;

namespace NitroGateway.CoyoteTests;

/// <summary>
/// ReliableProtocolDriver 不变量（对应 notes/Invariants/protocol-driver-pool.md I7 与 ADR-077/078）：
/// I7/G2：释放后到达的调用快速失败、不触达内层、不抛。
/// R1：同步 Dispose 不排水（不等在途调用）。
/// </summary>
internal static class ReliableDriverInvariants
{
    private static IProtocolDriver CreateReliable(IProtocolDriver inner) => new ReliableProtocolDriver(
        inner,
        NullLogger<ReliableProtocolDriver>.Instance,
        requestTimeout: TimeSpan.FromMinutes(10),
        maxRetryAttempts: 0);

    // ── I7/G2：释放后快速失败、不触达内层 ──

    public static Task I7_G2_Positive()
    {
        var inner = new CountingInner();
        return RunG2(CreateReliable(inner), inner);
    }

    /// <summary>负控：无释放守卫的坏装饰器，释放后仍透传内层 → 期望被抓。</summary>
    public static Task I7_G2_Negative_NoGuard()
    {
        var inner = new CountingInner();
        return RunG2(new NoGuardDriver(inner), inner);
    }

    private static async Task RunG2(IProtocolDriver driver, CountingInner inner)
    {
        driver.Dispose();

        var read = await driver.ReadBatchAsync([]);
        var write = await driver.WriteAsync(new DevicePoint { Name = "p", Address = "0" }, 1);
        var connect = await driver.ConnectAsync();

        if (!read.IsFailure || !write.IsFailure || !connect.IsFailure)
            throw new InvalidOperationException("I7/G2: 释放后调用应返回失败而非成功");

        if (inner.CallCount != 0)
            throw new InvalidOperationException($"I7/G2: 释放后不应触达内层，CallCount={inner.CallCount}");
    }

    // ── 释放幂等（同步 + 异步共用幂等位）──

    public static Task DisposeIdempotent_Positive()
    {
        var inner = new CountingInner();
        return RunDisposeIdempotent(CreateReliable(inner), inner);
    }

    /// <summary>负控：无幂等守卫的坏装饰器，并发/跨接口释放会重复拆内层 → 期望被抓。</summary>
    public static Task DisposeIdempotent_Negative_NonIdempotent()
    {
        var inner = new CountingInner();
        return RunDisposeIdempotent(new NonIdempotentDriver(inner), inner);
    }

    private static async Task RunDisposeIdempotent(IProtocolDriver driver, CountingInner inner)
    {
        var sync1 = Task.Run(driver.Dispose);
        var sync2 = Task.Run(driver.Dispose);
        var asyncDispose = Task.Run(async () => await driver.DisposeAsync());

        await Task.WhenAll(sync1, sync2, asyncDispose);

        if (inner.DisposeCount != 1)
            throw new InvalidOperationException($"释放幂等: 内层应恰释放一次，实际 {inner.DisposeCount}");
    }

    // ── R1：同步 Dispose 不排水 ──

    public static Task R1_Positive()
    {
        var inner = new BlockingInner();
        return RunR1(CreateReliable(inner), inner);
    }

    /// <summary>负控：带排水的坏实现，Dispose 与在途读互等 → 期望以死锁被抓。</summary>
    public static Task R1_Negative_Drain()
    {
        var inner = new BlockingInner();
        return RunR1(new DrainDriver(inner), inner);
    }

    private static async Task RunR1(IProtocolDriver driver, BlockingInner inner)
    {
        var read = Task.Run(() => driver.ReadBatchAsync([]));
        await inner.Entered;               // 在途读已开始

        var dispose = Task.Run(driver.Dispose);
        await dispose;                     // 若 Dispose 排水，则与 read 互等 → 死锁
        inner.Release();
        await read;
    }

    // ══════════════ 假内层 ══════════════

    /// <summary>计数内层：统计被触达次数与释放次数。</summary>
    private sealed class CountingInner : IProtocolDriver
    {
        private int _calls;
        private int _disposeCount;

        public int CallCount => Volatile.Read(ref _calls);
        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public DriverState State => DriverState.Connected;
        public DriverCapability Capability => new();

        public Task<OperationResult> ConnectAsync(CancellationToken ct = default)
            => Count(OperationResult.Success());
        public Task<OperationResult> DisconnectAsync(CancellationToken ct = default)
            => Count(OperationResult.Success());
        public Task<OperationResult> PingAsync(CancellationToken ct = default)
            => Count(OperationResult.Success());
        public Task<OperationResult<RawPointValue>> ReadAsync(DevicePoint point, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(OperationResult<RawPointValue>.Success(new RawPointValue { Point = point, Value = 0 }));
        }
        public Task<OperationResult<IReadOnlyList<RawPointValue>>> ReadBatchAsync(
            IEnumerable<DevicePoint> points, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(OperationResult<IReadOnlyList<RawPointValue>>.Success(Array.Empty<RawPointValue>()));
        }
        public Task<OperationResult> WriteAsync(DevicePoint point, object value, CancellationToken ct = default)
            => Count(OperationResult.Success());
        public Task<OperationResult> WriteBatchAsync(
            IEnumerable<KeyValuePair<DevicePoint, object>> entries, CancellationToken ct = default)
            => Count(OperationResult.Success());

        public void Dispose() => Interlocked.Increment(ref _disposeCount);
        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            return ValueTask.CompletedTask;
        }

        private Task<OperationResult> Count(OperationResult result)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(result);
        }
    }

    /// <summary>可阻塞内层：读进入时发信号，等待显式放行。</summary>
    private sealed class BlockingInner : IProtocolDriver
    {
        private readonly TaskCompletionSource _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;
        public void Release() => _release.TrySetResult();

        public DriverState State => DriverState.Connected;
        public DriverCapability Capability => new();

        public async Task<OperationResult<IReadOnlyList<RawPointValue>>> ReadBatchAsync(
            IEnumerable<DevicePoint> points, CancellationToken ct = default)
        {
            _entered.TrySetResult();
            await _release.Task;
            return OperationResult<IReadOnlyList<RawPointValue>>.Success(Array.Empty<RawPointValue>());
        }

        public Task<OperationResult> ConnectAsync(CancellationToken ct = default)
            => Task.FromResult(OperationResult.Success());
        public Task<OperationResult> DisconnectAsync(CancellationToken ct = default)
            => Task.FromResult(OperationResult.Success());
        public Task<OperationResult> PingAsync(CancellationToken ct = default)
            => Task.FromResult(OperationResult.Success());
        public Task<OperationResult<RawPointValue>> ReadAsync(DevicePoint point, CancellationToken ct = default)
            => Task.FromResult(OperationResult<RawPointValue>.Failure(OperationalError.Protocol("n/a")));
        public Task<OperationResult> WriteAsync(DevicePoint point, object value, CancellationToken ct = default)
            => Task.FromResult(OperationResult.Success());
        public Task<OperationResult> WriteBatchAsync(
            IEnumerable<KeyValuePair<DevicePoint, object>> entries, CancellationToken ct = default)
            => Task.FromResult(OperationResult.Success());
        public void Dispose() { }
    }

    // ══════════════ 坏实现（negative-control 种子） ══════════════

    /// <summary>无释放守卫：释放后仍透传内层。</summary>
    private sealed class NoGuardDriver(IProtocolDriver inner) : IProtocolDriver
    {
        public DriverState State => inner.State;
        public DriverCapability Capability => inner.Capability;
        public Task<OperationResult> ConnectAsync(CancellationToken ct = default) => inner.ConnectAsync(ct);
        public Task<OperationResult> DisconnectAsync(CancellationToken ct = default) => inner.DisconnectAsync(ct);
        public Task<OperationResult> PingAsync(CancellationToken ct = default) => inner.PingAsync(ct);
        public Task<OperationResult<RawPointValue>> ReadAsync(DevicePoint point, CancellationToken ct = default) => inner.ReadAsync(point, ct);
        public Task<OperationResult<IReadOnlyList<RawPointValue>>> ReadBatchAsync(IEnumerable<DevicePoint> points, CancellationToken ct = default) => inner.ReadBatchAsync(points, ct);
        public Task<OperationResult> WriteAsync(DevicePoint point, object value, CancellationToken ct = default) => inner.WriteAsync(point, value, ct);
        public Task<OperationResult> WriteBatchAsync(IEnumerable<KeyValuePair<DevicePoint, object>> entries, CancellationToken ct = default) => inner.WriteBatchAsync(entries, ct);
        public void Dispose() => inner.Dispose();
    }

    /// <summary>无幂等守卫：并发/跨接口释放会重复拆内层。</summary>
    private sealed class NonIdempotentDriver(IProtocolDriver inner) : IProtocolDriver
    {
        public DriverState State => inner.State;
        public DriverCapability Capability => inner.Capability;
        public Task<OperationResult> ConnectAsync(CancellationToken ct = default) => inner.ConnectAsync(ct);
        public Task<OperationResult> DisconnectAsync(CancellationToken ct = default) => inner.DisconnectAsync(ct);
        public Task<OperationResult> PingAsync(CancellationToken ct = default) => inner.PingAsync(ct);
        public Task<OperationResult<RawPointValue>> ReadAsync(DevicePoint point, CancellationToken ct = default) => inner.ReadAsync(point, ct);
        public Task<OperationResult<IReadOnlyList<RawPointValue>>> ReadBatchAsync(IEnumerable<DevicePoint> points, CancellationToken ct = default) => inner.ReadBatchAsync(points, ct);
        public Task<OperationResult> WriteAsync(DevicePoint point, object value, CancellationToken ct = default) => inner.WriteAsync(point, value, ct);
        public Task<OperationResult> WriteBatchAsync(IEnumerable<KeyValuePair<DevicePoint, object>> entries, CancellationToken ct = default) => inner.WriteBatchAsync(entries, ct);
        public void Dispose() => inner.Dispose();
        public ValueTask DisposeAsync()
        {
            inner.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>带排水的坏实现：Dispose 阻塞等待在途读完成。</summary>
    private sealed class DrainDriver(IProtocolDriver inner) : IProtocolDriver
    {
        private readonly TaskCompletionSource _drained =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _disposed;
        private int _inFlight;

        public DriverState State => inner.State;
        public DriverCapability Capability => inner.Capability;

        public async Task<OperationResult<IReadOnlyList<RawPointValue>>> ReadBatchAsync(
            IEnumerable<DevicePoint> points, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _inFlight);
            try
            {
                return await inner.ReadBatchAsync(points, ct);
            }
            finally
            {
                if (Interlocked.Decrement(ref _inFlight) == 0 && Volatile.Read(ref _disposed) != 0)
                    _drained.TrySetResult();
            }
        }

        public Task<OperationResult> ConnectAsync(CancellationToken ct = default) => inner.ConnectAsync(ct);
        public Task<OperationResult> DisconnectAsync(CancellationToken ct = default) => inner.DisconnectAsync(ct);
        public Task<OperationResult> PingAsync(CancellationToken ct = default) => inner.PingAsync(ct);
        public Task<OperationResult<RawPointValue>> ReadAsync(DevicePoint point, CancellationToken ct = default) => inner.ReadAsync(point, ct);
        public Task<OperationResult> WriteAsync(DevicePoint point, object value, CancellationToken ct = default) => inner.WriteAsync(point, value, ct);
        public Task<OperationResult> WriteBatchAsync(IEnumerable<KeyValuePair<DevicePoint, object>> entries, CancellationToken ct = default) => inner.WriteBatchAsync(entries, ct);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            if (Volatile.Read(ref _inFlight) != 0)
                _drained.Task.GetAwaiter().GetResult();   // 坏：排水阻塞
            inner.Dispose();
        }
    }
}
