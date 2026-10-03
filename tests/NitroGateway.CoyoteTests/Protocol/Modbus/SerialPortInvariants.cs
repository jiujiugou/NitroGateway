using HslCommunication.ModBus;
using Microsoft.Extensions.Logging.Abstractions;
using NitroGateway.Protocols.Modbus;

namespace NitroGateway.CoyoteTests;

/// <summary>
/// SerialPortManager 租约不变量（对应 notes/Invariants/modbus.md）：
/// I2：同端口多租约共享同一 Rtu + Gate（帧级串行前提）。
/// I3：引用计数账平——最后一个 Dispose 才移除端口；异参 Acquire 抛。
/// I5：租约 Dispose 幂等——并发释放只归还一次。
/// （I2 的"帧内 Station 一致"与 I4 换租约撕裂需抽象 ModbusRtu，暂列为非目标。）
/// </summary>
internal static class SerialPortInvariants
{
    private sealed class FakeConnectionFactory : ISerialPortConnectionFactory
    {
        private int _openCount;

        public int OpenCount => Volatile.Read(ref _openCount);

        public ModbusRtu Open(SerialPortSettings settings)
        {
            Interlocked.Increment(ref _openCount);
            return new ModbusRtu();   // 未打开：仅用于租约账目
        }
    }

    private static SerialPortSettings Settings(string port = "COM_TEST") => new() { PortName = port };

    private static SerialPortManager NewManager(FakeConnectionFactory factory)
        => new(NullLogger<SerialPortManager>.Instance, factory);

    // ── I2 + I3：共享句柄/闸门 + 引用计数账平 ──

    public static Task I2I3_Positive()
    {
        var factory = new FakeConnectionFactory();
        var manager = NewManager(factory);
        var settings = Settings();

        var l1 = manager.Acquire(settings);
        var l2 = manager.Acquire(settings);
        var l3 = manager.Acquire(settings);

        // I2：同端口共享同一 Rtu 与 Gate（串口只开一次）
        if (!ReferenceEquals(l1.Rtu, l2.Rtu) || !ReferenceEquals(l1.Rtu, l3.Rtu))
            throw new InvalidOperationException("I2: 同端口应共享同一 Rtu");
        if (!ReferenceEquals(l1.Gate, l2.Gate))
            throw new InvalidOperationException("I2: 同端口应共享同一 Gate");
        if (factory.OpenCount != 1)
            throw new InvalidOperationException($"I2: 串口只应打开一次，实际 {factory.OpenCount}");

        // I3：引用计数账平，最后一个释放才移除
        if (manager.GetStatus() is not [{ LeaseCount: 3 }])
            throw new InvalidOperationException("I3: 3 个租约后应为 LeaseCount=3");

        l1.Dispose();
        if (manager.GetStatus() is not [{ LeaseCount: 2 }])
            throw new InvalidOperationException("I3: 释放 1 个后应为 LeaseCount=2");

        l2.Dispose();
        if (manager.GetStatus() is not [{ LeaseCount: 1 }])
            throw new InvalidOperationException("I3: 释放 2 个后应为 LeaseCount=1");

        l3.Dispose();
        if (manager.GetStatus().Count != 0)
            throw new InvalidOperationException("I3: 最后一个释放后应移除端口");

        return Task.CompletedTask;
    }

    /// <summary>I3：同端口异参 Acquire 必须显式抛异常。</summary>
    public static Task I3_ParamMismatch_Throws()
    {
        var factory = new FakeConnectionFactory();
        var manager = NewManager(factory);
        var settings = Settings();
        manager.Acquire(settings);

        var threw = false;
        try
        {
            manager.Acquire(settings with { BaudRate = 19200 });
        }
        catch (InvalidOperationException)
        {
            threw = true;
        }

        if (!threw)
            throw new InvalidOperationException("I3: 异参 Acquire 应抛 InvalidOperationException");

        return Task.CompletedTask;
    }

    // ── I5：租约 Dispose 幂等（并发只归还一次）──

    public static async Task I5_ConcurrentDispose_ReleasesOnce()
    {
        var factory = new FakeConnectionFactory();
        var manager = NewManager(factory);
        var settings = Settings();

        var l1 = manager.Acquire(settings);
        var l2 = manager.Acquire(settings);   // LeaseCount = 2

        var disposals = Enumerable.Range(0, 8).Select(_ => Task.Run(l1.Dispose)).ToArray();
        await Task.WhenAll(disposals);

        // 幂等：l1 无论释放多少次只归还一次 → 仍剩 l2
        if (manager.GetStatus() is not [{ LeaseCount: 1 }])
            throw new InvalidOperationException("I5: 租约并发释放应只归还一次");

        l2.Dispose();
        if (manager.GetStatus().Count != 0)
            throw new InvalidOperationException("I5: 最后租约释放后应移除端口");
    }
}
