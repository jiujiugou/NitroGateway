using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Protocols;
using NitroGateway.Protocol.Abstractions;
using NitroGateway.Protocols.Modbus;
using NitroGateway.Shared;
using Xunit;

namespace NitroGateway.UnitTests.Protocol;

/// <summary>
/// ADR-074：串行化所有权在<b>各具体驱动实例自身的闸门</b>。
/// 装饰器（ReliableProtocolDriver）不再持有建连闸门；写路径的显式建连与读路径的自动建连
/// 都落到内层驱动的同一把 <see cref="ModbusDriverBase.Gate"/> 上串行。
/// </summary>
public class ProtocolDriverGateConcurrencyTests
{
    /// <summary>
    /// 显式 ConnectAsync（写/配置路径）与 ReadBatchAsync 触发的自动建连并发时，
    /// 驱动闸门内的受保护体绝不能被同时进入。
    /// <para>若闸门失效（如建连不走 <c>GuardedAsync</c>），两路会同时进入，计数达 2。</para>
    /// </summary>
    [Fact]
    public async Task ExplicitConnect_ConcurrentWithReadAutoConnect_SerializedByDriverGate()
    {
        var inner = new GuardedModbusTestDriver(NullLogger<GuardedModbusTestDriver>.Instance);
        var driver = new ReliableProtocolDriver(
            inner,
            NullLogger<ReliableProtocolDriver>.Instance,
            requestTimeout: TimeSpan.FromSeconds(5),
            maxRetryAttempts: 0,
            retryDelay: TimeSpan.FromMilliseconds(1));

        var explicitConnect = Task.Run(() => driver.ConnectAsync());
        var readTriggeredConnect = Task.Run(() => driver.ReadBatchAsync([]));

        await Task.WhenAll(explicitConnect, readTriggeredConnect);

        Assert.Equal(1, inner.MaxConcurrentGuardedOperations);
    }

    /// <summary>
    /// 受闸门保护的 Modbus 测试驱动：Connect 走基类 <c>GuardedAsync</c>（与真实
    /// <c>ModbusTcpDriver</c> 同款路径），用有界 rendezvous 检测是否有第二个操作同时进入。
    /// 串行时恒为 1；未被串行则为 2。有界等待保证即使单路进入也不挂起测试。
    /// </summary>
    private sealed class GuardedModbusTestDriver : ModbusDriverBase
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly CountdownEvent _rendezvous = new(2);
        private int _concurrent;
        private int _maxConcurrent;

        public GuardedModbusTestDriver(ILogger logger) : base(logger) { }

        protected override SemaphoreSlim Gate => _gate;

        public int MaxConcurrentGuardedOperations => Volatile.Read(ref _maxConcurrent);

        public override Task<OperationResult> ConnectAsync(CancellationToken ct = default)
            => GuardedAsync(async token =>
            {
                if (State == DriverState.Connected)   // 闸门内双检（与真实驱动一致）
                    return OperationResult.Success();

                UpdateMax(Interlocked.Increment(ref _concurrent));
                try
                {
                    _rendezvous.Signal();
                    await Task.Run(() => _rendezvous.Wait(TimeSpan.FromMilliseconds(400)), token);
                    State = DriverState.Connected;
                    return OperationResult.Success();
                }
                finally
                {
                    Interlocked.Decrement(ref _concurrent);
                }
            }, ct);

        public override Task<OperationResult> DisconnectAsync(CancellationToken ct = default)
            => GuardedAsync(_ => Task.FromResult(OperationResult.Success()), ct);

        protected override Task<object[]?> ReadBatchTypedAsync(string address, DataType type, int count)
            => throw new NotSupportedException();

        protected override Task<object> ReadSingleTypedAsync(DataType type, string address)
            => Task.FromResult<object>((short)0);

        protected override Task<OperationResult> WriteSingleValueAsync(DevicePoint point, string address, object value)
            => Task.FromResult(OperationResult.Success());

        private void UpdateMax(int value)
        {
            int current;
            while (value > (current = Volatile.Read(ref _maxConcurrent)))
            {
                if (Interlocked.CompareExchange(ref _maxConcurrent, value, current) == current)
                    return;
            }
        }

        protected override void DisposeCore() { }
    }
}
