using HslCommunication;
using HslCommunication.ModBus;
using Microsoft.Extensions.Logging.Abstractions;
using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Protocols;
using NitroGateway.Protocols.Modbus;
using NitroGateway.Shared;

namespace NitroGateway.CoyoteTests;

/// <summary>
/// ModbusRtuDriver 不变量（对应 notes/Invariants/modbus.md I3/X2）：
/// I3：并发 Connect/Disconnect/Dispose 下，串口租约引用计数账平——最终无残留租约、每个打开的串口恰释放一次。
/// X2：异步 <see cref="IAsyncDisposable.DisposeAsync"/> 走 DisconnectAsync（取 _sync + 共享闸门，等在途帧）；
/// 同步 Dispose 不排水（ADR-077）。
/// <para>用现有 <see cref="ISerialPortManager"/> 测试缝 + 内部 <see cref="SerialPortLease"/> 支撑的
/// <see cref="FakeRtuFactory"/> 返回可阻塞/可观测的 <see cref="FakeModbusRtu"/>（Hsl <c>ReadInt16Async</c>/<c>Dispose(bool)</c> 可覆写）。
/// 帧级 Station 一致（I2）与换租约撕裂（I4/X3）需抽象 ModbusRtu，列为非目标。</para>
/// </summary>
internal static class ModbusRtuDriverInvariants
{
    private static DeviceConnection RtuConn() => new() { Endpoint = "COM_TEST" };

    private static SerialPortSettings RtuSettings() => new() { PortName = "COM_TEST" };

    private static DevicePoint Point => new()
    {
        Id = Guid.NewGuid(),
        Name = "P",
        Address = "40001",
        DataType = DataType.Int16
    };

    private static SerialPortManager NewManager(FakeRtuFactory factory)
        => new(NullLogger<SerialPortManager>.Instance, factory);

    // ── I3：并发生命周期后租约账平、串口恰释放一次 ──

    public static Task I3_LeaseAccounted_Positive()
    {
        var factory = new FakeRtuFactory();
        var manager = NewManager(factory);
        var driver = new ModbusRtuDriver(RtuConn(), manager, NullLogger.Instance);
        return RunLeaseAccounting(driver, manager, factory);
    }

    /// <summary>负控：Connect 只取不还的坏驱动，最终应残留租约 → 期望被抓。</summary>
    public static Task I3_LeaseAccounted_Negative_Leaky()
    {
        var factory = new FakeRtuFactory();
        var manager = NewManager(factory);
        var driver = new LeakyRtuDriver(manager, RtuSettings());
        return RunLeaseAccounting(driver, manager, factory);
    }

    private static async Task RunLeaseAccounting(
        IProtocolDriver driver, SerialPortManager manager, FakeRtuFactory factory)
    {
        var ops = new Func<Task>[]
        {
            () => driver.ConnectAsync(),
            () => driver.ConnectAsync(),
            () => driver.DisconnectAsync(),
            () => driver.ConnectAsync(),
            () => Task.Run(driver.Dispose),
            () => Task.Run(async () => await driver.DisposeAsync()),
            () => driver.DisconnectAsync(),
            () => driver.ConnectAsync(),
        };

        await Task.WhenAll(ops.Select(op => Task.Run(op)));
        await driver.DisconnectAsync();   // 收尾断开

        var leases = manager.GetStatus().Sum(s => s.LeaseCount);
        if (leases != 0)
            throw new InvalidOperationException($"I3(RTU): 断开后不应残留租约，实际 {leases}");

        foreach (var rtu in factory.Rtus)
        {
            if (rtu.DisposeCount != 1)
                throw new InvalidOperationException($"I3(RTU): 串口应恰释放一次，实际 {rtu.DisposeCount}");
        }
    }

    // ── X2：异步释放排水（取共享闸门等在途帧）──

    public static async Task X2_AsyncDisposeDrains_Positive()
    {
        var factory = new FakeRtuFactory();
        var manager = NewManager(factory);
        var driver = new ModbusRtuDriver(RtuConn(), manager, NullLogger.Instance);

        await driver.ConnectAsync();
        var rtu = factory.Last;
        rtu.BlockReads = true;

        await RunAsyncDisposeDrains(driver, rtu);
    }

    /// <summary>负控：异步释放不取闸门、直接归还租约 → 在途帧未完成即拆串口，期望被抓。</summary>
    public static async Task X2_AsyncDisposeDrains_Negative_NoDrain()
    {
        var factory = new FakeRtuFactory();
        var manager = NewManager(factory);
        var driver = new NonDrainingRtuDriver(manager, RtuSettings());

        await driver.ConnectAsync();
        var rtu = factory.Last;
        rtu.BlockReads = true;

        await RunAsyncDisposeDrains(driver, rtu);
    }

    private static async Task RunAsyncDisposeDrains(IProtocolDriver driver, FakeModbusRtu rtu)
    {
        var read = Task.Run(() => driver.ReadAsync(Point));
        await rtu.Entered;

        var dispose = Task.Run(async () => await driver.DisposeAsync());
        for (var i = 0; i < 3; i++) await Task.Yield();

        if (rtu.DisposeCount != 0)
            throw new InvalidOperationException("X2(RTU): 异步释放不应在途帧未完成时拆除串口");

        rtu.Release();
        await read;
        await dispose;

        if (rtu.DisposeCount != 1)
            throw new InvalidOperationException($"X2(RTU): 异步释放应恰拆除一次，实际 {rtu.DisposeCount}");
    }

    // ══════════════ 测试替身 ══════════════

    /// <summary>返回可观测 <see cref="FakeModbusRtu"/> 的串口连接工厂。</summary>
    private sealed class FakeRtuFactory : ISerialPortConnectionFactory
    {
        private readonly List<FakeModbusRtu> _rtus = new();

        public IReadOnlyList<FakeModbusRtu> Rtus => _rtus;
        public FakeModbusRtu Last => _rtus[^1];

        public ModbusRtu Open(SerialPortSettings settings)
        {
            var rtu = new FakeModbusRtu();
            _rtus.Add(rtu);
            return rtu;
        }
    }

    /// <summary>可阻塞、可观测并发峰值与释放时机的 ModbusRtu 替身。</summary>
    private sealed class FakeModbusRtu : ModbusRtu
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

    /// <summary>只取租约、从不归还的坏驱动（泄漏）。</summary>
    private sealed class LeakyRtuDriver(ISerialPortManager manager, SerialPortSettings settings) : IProtocolDriver
    {
        private SerialPortLease? _lease;

        public DriverState State => DriverState.Connected;
        public DriverCapability Capability => new();

        public Task<OperationResult> ConnectAsync(CancellationToken ct = default)
        {
            _lease ??= manager.Acquire(settings);   // 坏：不替换、不释放
            return Task.FromResult(OperationResult.Success());
        }

        public Task<OperationResult> DisconnectAsync(CancellationToken ct = default)
            => Task.FromResult(OperationResult.Success());   // 坏：不释放
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

        public void Dispose() { }   // 坏：不释放
    }

    /// <summary>异步释放不排水的坏驱动：读走共享句柄，释放时不取闸门直接归还租约。</summary>
    private sealed class NonDrainingRtuDriver(ISerialPortManager manager, SerialPortSettings settings) : IProtocolDriver
    {
        private SerialPortLease? _lease;

        public DriverState State => DriverState.Connected;
        public DriverCapability Capability => new();

        public Task<OperationResult> ConnectAsync(CancellationToken ct = default)
        {
            _lease = manager.Acquire(settings);
            return Task.FromResult(OperationResult.Success());
        }

        public async Task<OperationResult<RawPointValue>> ReadAsync(DevicePoint point, CancellationToken ct = default)
        {
            await _lease!.Rtu.ReadInt16Async("0", 1);
            return OperationResult<RawPointValue>.Success(
                new RawPointValue { Point = point, Value = (short)0 });
        }

        public Task<OperationResult> DisconnectAsync(CancellationToken ct = default)
            => Task.FromResult(OperationResult.Success());
        public Task<OperationResult> PingAsync(CancellationToken ct = default)
            => Task.FromResult(OperationResult.Success());
        public Task<OperationResult<IReadOnlyList<RawPointValue>>> ReadBatchAsync(
            IEnumerable<DevicePoint> points, CancellationToken ct = default)
            => Task.FromResult(OperationResult<IReadOnlyList<RawPointValue>>.Success(Array.Empty<RawPointValue>()));
        public Task<OperationResult> WriteAsync(DevicePoint point, object value, CancellationToken ct = default)
            => Task.FromResult(OperationResult.Success());
        public Task<OperationResult> WriteBatchAsync(
            IEnumerable<KeyValuePair<DevicePoint, object>> entries, CancellationToken ct = default)
            => Task.FromResult(OperationResult.Success());

        public void Dispose() => _lease?.Dispose();

        public ValueTask DisposeAsync()
        {
            _lease?.Dispose();   // 坏：不取闸门、不等在途帧
            return ValueTask.CompletedTask;
        }
    }
}
