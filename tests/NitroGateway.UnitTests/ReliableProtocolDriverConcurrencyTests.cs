using Microsoft.Extensions.Logging.Abstractions;
using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Protocols;
using NitroGateway.Protocol.Abstractions;
using NitroGateway.Shared;
using Xunit;

namespace NitroGateway.UnitTests;

/// <summary>
/// <see cref="ReliableProtocolDriver"/> 并发测试。
///
/// <para><b>两条不变量（ADR-074 / ADR-077）：</b></para>
/// <list type="bullet">
/// <item>并发单飞：多条并发读在同一未连接窗口内，真正建连只发生一次（驱动自身闸门双检）。</item>
/// <item>释放边界（ADR-077）：<c>Dispose</c> <b>不排水</b>（不等在途调用），但释放后到达的调用
/// 必须快速失败、不抛异常。</item>
/// </list>
/// </summary>
public class ReliableProtocolDriverConcurrencyTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task ConcurrentReads_OnDisconnectedInner_ShouldConnectOnlyOnce()
    {
        var inner = new SlowConnectInner(TimeSpan.FromMilliseconds(100));
        var driver = new ReliableProtocolDriver(
            inner,
            NullLogger<ReliableProtocolDriver>.Instance,
            requestTimeout: TimeSpan.FromSeconds(5),
            maxRetryAttempts: 0);                       // 关重试，隔离出"自动建连"这一条路径

        var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var readers = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            await start.Task.ConfigureAwait(false);
            await driver.ReadBatchAsync([]);
        })).ToArray();

        start.SetResult(true);
        await Task.WhenAll(readers).WaitAsync(Timeout);

        // 期望：驱动闸门内双检 → 8 条并发读只真正建连一次
        Assert.Equal(1, inner.ConnectCalls);
    }

    /// <summary>
    /// G2（释放后干净失败）：<c>Dispose</c> 之后的调用必须返回失败结果，且不抛异常。
    /// <para><b>负控</b>：见 <see cref="NegativeControl_NoGuardWrapper_AfterDisposeStillSucceeds"/>。</para>
    /// </summary>
    [Fact]
    public async Task AfterDispose_Calls_ReturnFailure_NotThrow()
    {
        var inner = new BlockingReadInner(blockReads: false);
        var driver = new ReliableProtocolDriver(
            inner, NullLogger<ReliableProtocolDriver>.Instance, maxRetryAttempts: 0);

        driver.Dispose();

        var read = await driver.ReadBatchAsync([]);
        var write = await driver.WriteAsync(new DevicePoint { Name = "p", Address = "0" }, 1);
        var connect = await driver.ConnectAsync();

        Assert.True(read.IsFailure);
        Assert.True(write.IsFailure);
        Assert.True(connect.IsFailure);
    }

    /// <summary>
    /// R1（ADR-077 防回退）：在途读未完成时调用 <c>Dispose</c>，<b>必须立即返回</b>（不排水）。
    /// <para>作用：若有人重新引入"等排空"（曾导致桌面 UI 死锁），本测试会失败。</para>
    /// <para><b>负控</b>：见 <see cref="NegativeControl_DrainWrapper_DisposeBlocksOnInFlight"/>。</para>
    /// </summary>
    [Fact]
    public async Task Dispose_DoesNotBlockOnInFlightOperation()
    {
        var inner = new BlockingReadInner(blockReads: true);
        var driver = new ReliableProtocolDriver(
            inner, NullLogger<ReliableProtocolDriver>.Instance, maxRetryAttempts: 0);

        Assert.True(await DisposeCompletesWhileReadBlocked(driver, inner));
    }

    /// <summary>负控：无释放守卫的坏实现 → 释放后调用仍成功，证明 G2 断言有能力变红。</summary>
    [Fact]
    public async Task NegativeControl_NoGuardWrapper_AfterDisposeStillSucceeds()
    {
        var inner = new BlockingReadInner(blockReads: false);
        var broken = new NoGuardWrapper(inner);

        broken.Dispose();

        var read = await broken.ReadBatchAsync([]);
        Assert.True(read.IsSuccess);            // 坏实现：未失败 = G2 会红
    }

    /// <summary>负控：带排水（阻塞等待在途）的坏实现 → Dispose 提前不返回，证明 R1 断言有能力变红。</summary>
    [Fact]
    public async Task NegativeControl_DrainWrapper_DisposeBlocksOnInFlight()
    {
        var inner = new BlockingReadInner(blockReads: true);
        var broken = new DrainWrapper(inner);

        Assert.False(await DisposeCompletesWhileReadBlocked(broken, inner));
    }

    /// <summary>
    /// 起一条在途读 → 调 <c>Dispose</c> → 返回「Dispose 是否在 500ms 内完成」。
    /// 完成后放行读并等待，避免遗留阻塞任务。
    /// </summary>
    private static async Task<bool> DisposeCompletesWhileReadBlocked(IProtocolDriver driver, BlockingReadInner inner)
    {
        var read = Task.Run(() => driver.ReadBatchAsync([]));
        await inner.Entered.WaitAsync(Timeout);

        var dispose = Task.Run(driver.Dispose);
        var completed = await Task.WhenAny(dispose, Task.Delay(500)) == dispose;

        inner.Release();                         // 放行读，避免遗留阻塞任务
        await dispose.WaitAsync(Timeout).ConfigureAwait(false);
        await read.WaitAsync(Timeout).ConfigureAwait(false);
        return completed;
    }

    /// <summary>
    /// 可编程内层驱动：初始"未连接"，<c>ConnectAsync</c> 自持闸门 + 门内双检（ADR-074 契约），
    /// 故意变慢以制造并发窗口，只统计<b>真正发起建连</b>的次数。
    /// </summary>
    private sealed class SlowConnectInner : IProtocolDriver
    {
        private readonly TimeSpan _connectDelay;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private int _connectCalls;

        public SlowConnectInner(TimeSpan connectDelay) => _connectDelay = connectDelay;

        /// <summary>实际发起建连的次数；驱动契约正确时应为 1。</summary>
        public int ConnectCalls => Volatile.Read(ref _connectCalls);

        public DriverState State { get; private set; } = DriverState.Disconnected;
        public DriverCapability Capability { get; } = new();

        public async Task<OperationResult> ConnectAsync(CancellationToken ct = default)
        {
            await _gate.WaitAsync(ct);
            try
            {
                if (State == DriverState.Connected)     // 闸门内双检：并发调用只真正连一次
                    return OperationResult.Success();

                Interlocked.Increment(ref _connectCalls);
                await Task.Delay(_connectDelay, ct);    // 制造窗口：让其它读也看到"未连接"
                State = DriverState.Connected;
                return OperationResult.Success();
            }
            finally
            {
                _gate.Release();
            }
        }

        public Task<OperationResult> DisconnectAsync(CancellationToken ct = default)
            => Task.FromResult(OperationResult.Success());
        public Task<OperationResult> PingAsync(CancellationToken ct = default)
            => Task.FromResult(OperationResult.Success());
        public Task<OperationResult<RawPointValue>> ReadAsync(DevicePoint point, CancellationToken ct = default)
            => Task.FromResult(OperationResult<RawPointValue>.Failure(OperationalError.Protocol("n/a")));
        public Task<OperationResult<IReadOnlyList<RawPointValue>>> ReadBatchAsync(
            IEnumerable<DevicePoint> points, CancellationToken ct = default)
            => Task.FromResult(OperationResult<IReadOnlyList<RawPointValue>>.Success(Array.Empty<RawPointValue>()));
        public Task<OperationResult> WriteAsync(DevicePoint point, object value, CancellationToken ct = default)
            => Task.FromResult(OperationResult.Success());
        public Task<OperationResult> WriteBatchAsync(
            IEnumerable<KeyValuePair<DevicePoint, object>> entries, CancellationToken ct = default)
            => Task.FromResult(OperationResult.Success());
        public void Dispose() { }
    }

    /// <summary>内层读可配置为阻塞的假驱动：进入时发信号，等待显式放行；统计 Dispose 次数。</summary>
    private sealed class BlockingReadInner : IProtocolDriver
    {
        private readonly bool _blockReads;
        private readonly TaskCompletionSource _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _disposeCalls;

        public BlockingReadInner(bool blockReads) => _blockReads = blockReads;

        public Task Entered => _entered.Task;
        public int DisposeCalls => Volatile.Read(ref _disposeCalls);
        public void Release() => _release.TrySetResult();

        public DriverState State { get; private set; } = DriverState.Connected;
        public DriverCapability Capability { get; } = new();

        public async Task<OperationResult<IReadOnlyList<RawPointValue>>> ReadBatchAsync(
            IEnumerable<DevicePoint> points, CancellationToken ct = default)
        {
            _entered.TrySetResult();
            if (_blockReads)
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
        public void Dispose() => Interlocked.Increment(ref _disposeCalls);
    }

    /// <summary>seeded fault：直接透传、无释放守卫的坏实现，用于 G2 负控。</summary>
    private sealed class NoGuardWrapper : IProtocolDriver
    {
        private readonly BlockingReadInner _inner;
        public NoGuardWrapper(BlockingReadInner inner) => _inner = inner;

        public DriverState State => _inner.State;
        public DriverCapability Capability => _inner.Capability;
        public Task<OperationResult> ConnectAsync(CancellationToken ct = default) => _inner.ConnectAsync(ct);
        public Task<OperationResult> DisconnectAsync(CancellationToken ct = default) => _inner.DisconnectAsync(ct);
        public Task<OperationResult> PingAsync(CancellationToken ct = default) => _inner.PingAsync(ct);
        public Task<OperationResult<RawPointValue>> ReadAsync(DevicePoint point, CancellationToken ct = default)
            => _inner.ReadAsync(point, ct);
        public Task<OperationResult<IReadOnlyList<RawPointValue>>> ReadBatchAsync(
            IEnumerable<DevicePoint> points, CancellationToken ct = default) => _inner.ReadBatchAsync(points, ct);
        public Task<OperationResult> WriteAsync(DevicePoint point, object value, CancellationToken ct = default)
            => _inner.WriteAsync(point, value, ct);
        public Task<OperationResult> WriteBatchAsync(
            IEnumerable<KeyValuePair<DevicePoint, object>> entries, CancellationToken ct = default)
            => _inner.WriteBatchAsync(entries, ct);
        public void Dispose() => _inner.Dispose();
    }

    /// <summary>seeded fault：带排水（Dispose 阻塞等在途）的坏实现，用于 R1 负控（对应已回退的方案）。</summary>
    private sealed class DrainWrapper : IProtocolDriver
    {
        private readonly BlockingReadInner _inner;
        private readonly TaskCompletionSource _drained =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _disposed;
        private int _inFlight;

        public DrainWrapper(BlockingReadInner inner) => _inner = inner;

        public DriverState State => _inner.State;
        public DriverCapability Capability => _inner.Capability;
        public Task<OperationResult> ConnectAsync(CancellationToken ct = default) => _inner.ConnectAsync(ct);
        public Task<OperationResult> DisconnectAsync(CancellationToken ct = default) => _inner.DisconnectAsync(ct);
        public Task<OperationResult> PingAsync(CancellationToken ct = default) => _inner.PingAsync(ct);
        public Task<OperationResult<RawPointValue>> ReadAsync(DevicePoint point, CancellationToken ct = default)
            => _inner.ReadAsync(point, ct);
        public Task<OperationResult> WriteAsync(DevicePoint point, object value, CancellationToken ct = default)
            => _inner.WriteAsync(point, value, ct);
        public Task<OperationResult> WriteBatchAsync(
            IEnumerable<KeyValuePair<DevicePoint, object>> entries, CancellationToken ct = default)
            => _inner.WriteBatchAsync(entries, ct);

        public async Task<OperationResult<IReadOnlyList<RawPointValue>>> ReadBatchAsync(
            IEnumerable<DevicePoint> points, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _inFlight);
            try { return await _inner.ReadBatchAsync(points, ct); }
            finally
            {
                if (Interlocked.Decrement(ref _inFlight) == 0 && Volatile.Read(ref _disposed) != 0)
                    _drained.TrySetResult();
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            if (Volatile.Read(ref _inFlight) != 0)
                _drained.Task.GetAwaiter().GetResult();   // 坏：排水阻塞
            _inner.Dispose();
        }
    }
}
