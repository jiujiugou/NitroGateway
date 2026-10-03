using HslCommunication;
using HslCommunication.ModBus;
using Microsoft.Extensions.Logging.Abstractions;
using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Protocols;
using NitroGateway.Protocols.Modbus;
using NitroGateway.Shared;

namespace NitroGateway.CoyoteTests;

/// <summary>
/// ModbusTcpDriver 不变量（对应 notes/Invariants/modbus.md I1/X1）：
/// I1：派生驱动所有受保护操作经自身闸门串行（ADR-074）。
/// X1：异步 <see cref="IAsyncDisposable.DisposeAsync"/> 取闸门、等在途操作；同步 Dispose 不排水（ADR-077）。
/// <para>用内部构造缝注入 <see cref="FakeModbusTcpClient"/>（Hsl <c>ReadInt16Async</c>/<c>Dispose(bool)</c> 可覆写），
/// 从而无需真实网络即可受控阻塞在途读。<c>ConnectServerAsync</c> 非 virtual，故建连不在本文件覆盖范围。</para>
/// </summary>
internal static class ModbusTcpDriverInvariants
{
    private const int Parallelism = 8;

    private static DeviceConnection Conn() => new() { Endpoint = "192.168.1.1:502" };

    private static DevicePoint Point => new()
    {
        Id = Guid.NewGuid(),
        Name = "P",
        Address = "40001",
        DataType = DataType.Int16
    };

    private static ModbusTcpDriver NewDriver(FakeModbusTcpClient client)
        => new(Conn(), NullLogger.Instance, client);

    // ── I1：读操作经驱动闸门串行 ──

    public static Task I1_ReadSerialized_Positive()
    {
        var client = new FakeModbusTcpClient();
        var driver = NewDriver(client);
        return RunReadSerialized(() => driver.ReadAsync(Point), client);
    }

    /// <summary>负控：无闸门的坏驱动，受控在途读应并发进入 → 期望被抓。</summary>
    public static Task I1_ReadSerialized_Negative_NoGate()
    {
        var client = new FakeModbusTcpClient();
        var driver = new UnguardedTcpDriver(client);
        return RunReadSerialized(() => driver.ReadAsync(Point), client);
    }

    private static async Task RunReadSerialized(Func<Task> read, FakeModbusTcpClient client)
    {
        var tasks = Enumerable.Range(0, Parallelism).Select(_ => Task.Run(read)).ToArray();
        await Task.WhenAll(tasks);

        if (client.MaxConcurrency > 1)
            throw new InvalidOperationException(
                $"I1(TCP): 读应经闸门串行，实际并发进入 {client.MaxConcurrency}");
    }

    // ── X1：异步释放排水（取闸门等在途读）──

    public static Task X1_AsyncDisposeDrains_Positive()
    {
        var client = new FakeModbusTcpClient { BlockReads = true };
        var driver = NewDriver(client);
        return RunAsyncDisposeDrains(driver, client);
    }

    /// <summary>负控：异步释放不取闸门、直接拆客户端 → 在途读未完成即拆除，期望被抓。</summary>
    public static Task X1_AsyncDisposeDrains_Negative_NoDrain()
    {
        var client = new FakeModbusTcpClient { BlockReads = true };
        var driver = new NonDrainingAsyncDisposeDriver(client);
        return RunAsyncDisposeDrains(driver, client);
    }

    private static async Task RunAsyncDisposeDrains(IProtocolDriver driver, FakeModbusTcpClient client)
    {
        var read = Task.Run(() => driver.ReadAsync(Point));
        await client.Entered;

        var dispose = Task.Run(async () => await driver.DisposeAsync());
        for (var i = 0; i < 3; i++) await Task.Yield();

        if (client.DisposeCount != 0)
            throw new InvalidOperationException("X1(TCP): 异步释放不应在途读未完成时拆除客户端");

        client.Release();
        await read;
        await dispose;

        if (client.DisposeCount != 1)
            throw new InvalidOperationException($"X1(TCP): 异步释放应恰拆除一次，实际 {client.DisposeCount}");
    }

    // ══════════════ 假客户端 ══════════════

    /// <summary>可阻塞、可观测并发峰值的 ModbusTcpNet 替身；只覆写读与释放（其余走真实基类）。</summary>
    private sealed class FakeModbusTcpClient : ModbusTcpNet
    {
        private readonly TaskCompletionSource _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _active;
        private int _max;
        private int _disposeCount;
        private int _disposedWhileActive;

        public bool BlockReads { get; set; }
        public Task Entered => _entered.Task;
        public void Release() => _release.TrySetResult();
        public int MaxConcurrency => Volatile.Read(ref _max);
        public int DisposeCount => Volatile.Read(ref _disposeCount);
        public bool DisposedWhileActive => Volatile.Read(ref _disposedWhileActive) != 0;

        public override async Task<OperateResult<short[]>> ReadInt16Async(string address, ushort length)
        {
            var active = Interlocked.Increment(ref _active);
            UpdateMax(active);
            _entered.TrySetResult();
            await Task.Yield();
            if (BlockReads) await _release.Task;
            Interlocked.Decrement(ref _active);
            return new OperateResult<short[]> { IsSuccess = true, Content = new short[length] };
        }

        protected override void Dispose(bool disposing)
        {
            if (Volatile.Read(ref _active) != 0)
                Interlocked.Exchange(ref _disposedWhileActive, 1);
            Interlocked.Increment(ref _disposeCount);
            base.Dispose(disposing);
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

    // ══════════════ 坏实现（negative-control 种子） ══════════════

    /// <summary>无闸门坏驱动：读直接打客户端。</summary>
    private sealed class UnguardedTcpDriver(FakeModbusTcpClient client) : IProtocolDriver
    {
        public DriverState State => DriverState.Connected;
        public DriverCapability Capability => new();

        public Task<OperationResult> ConnectAsync(CancellationToken ct = default)
            => Task.FromResult(OperationResult.Success());
        public Task<OperationResult> DisconnectAsync(CancellationToken ct = default)
            => Task.FromResult(OperationResult.Success());
        public Task<OperationResult> PingAsync(CancellationToken ct = default)
            => Task.FromResult(OperationResult.Success());
        public Task<OperationResult> WriteAsync(DevicePoint point, object value, CancellationToken ct = default)
            => Task.FromResult(OperationResult.Success());
        public Task<OperationResult> WriteBatchAsync(
            IEnumerable<KeyValuePair<DevicePoint, object>> entries, CancellationToken ct = default)
            => Task.FromResult(OperationResult.Success());

        public async Task<OperationResult<RawPointValue>> ReadAsync(DevicePoint point, CancellationToken ct = default)
        {
            var r = await client.ReadInt16Async("0", 1);   // 坏：无闸门
            return OperationResult<RawPointValue>.Success(
                new RawPointValue { Point = point, Value = r.Content[0] });
        }

        public async Task<OperationResult<IReadOnlyList<RawPointValue>>> ReadBatchAsync(
            IEnumerable<DevicePoint> points, CancellationToken ct = default)
        {
            await client.ReadInt16Async("0", 1);           // 坏：无闸门
            return OperationResult<IReadOnlyList<RawPointValue>>.Success(Array.Empty<RawPointValue>());
        }

        public void Dispose() { }
    }

    /// <summary>异步释放不排水的坏驱动：直接拆客户端（模拟 X1 回归）。读走客户端以制造在途。</summary>
    private sealed class NonDrainingAsyncDisposeDriver(FakeModbusTcpClient client) : IProtocolDriver
    {
        public DriverState State => DriverState.Connected;
        public DriverCapability Capability => new();

        public Task<OperationResult> ConnectAsync(CancellationToken ct = default)
            => Task.FromResult(OperationResult.Success());
        public Task<OperationResult> DisconnectAsync(CancellationToken ct = default)
            => Task.FromResult(OperationResult.Success());
        public Task<OperationResult> PingAsync(CancellationToken ct = default)
            => Task.FromResult(OperationResult.Success());
        public Task<OperationResult> WriteAsync(DevicePoint point, object value, CancellationToken ct = default)
            => Task.FromResult(OperationResult.Success());
        public Task<OperationResult> WriteBatchAsync(
            IEnumerable<KeyValuePair<DevicePoint, object>> entries, CancellationToken ct = default)
            => Task.FromResult(OperationResult.Success());

        public async Task<OperationResult<RawPointValue>> ReadAsync(DevicePoint point, CancellationToken ct = default)
        {
            var r = await client.ReadInt16Async("0", 1);
            return OperationResult<RawPointValue>.Success(
                new RawPointValue { Point = point, Value = r.Content[0] });
        }

        public Task<OperationResult<IReadOnlyList<RawPointValue>>> ReadBatchAsync(
            IEnumerable<DevicePoint> points, CancellationToken ct = default)
            => Task.FromResult(OperationResult<IReadOnlyList<RawPointValue>>.Success(Array.Empty<RawPointValue>()));

        public void Dispose() => client.Dispose();

        public ValueTask DisposeAsync()
        {
            client.Dispose();   // 坏：不等在途读
            return ValueTask.CompletedTask;
        }
    }
}
