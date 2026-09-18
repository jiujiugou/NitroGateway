using Microsoft.Extensions.Logging.Abstractions;
using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Protocols;
using NitroGateway.Protocol.Abstractions;
using NitroGateway.Shared;
using Xunit;

namespace NitroGateway.UnitTests;

/// <summary>
/// 驱动自动建连的<b>并发单飞</b>测试（ADR-074：串行化所有权在<b>各具体驱动实例自身的闸门</b>）。
///
/// <para><b>不变量</b>：多条并发读在同一未连接窗口内，装饰器会把 <c>ConnectAsync</c> 转调内层多次，
/// 但<b>真正建连只发生一次</b>——由驱动自身闸门内的双检保证。装饰器不再持有建连闸门。</para>
///
/// <para><b>认证方式</b>：内层驱动自持闸门并双检；8 条并发读下 <c>ConnectCalls</c>（实际建连次数）
/// 应恒为 1。若驱动去掉闸门/双检（违反 ADR-074 契约），则会变为并发数。</para>
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

        // 起跑闸门：所有读同时冲入，最大化"都看到未连接"的窗口
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
}
