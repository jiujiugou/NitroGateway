using HslCommunication.ModBus;
using HslCommunication.Core;
using Microsoft.Extensions.Logging;
using System.IO.Ports;

namespace NitroGateway.Protocols.Modbus;

/// <summary>串口参数设置</summary>
public sealed record SerialPortSettings
{
    public required string PortName { get; init; }
    public int BaudRate { get; init; } = 9600;
    public int DataBits { get; init; } = 8;
    public Parity Parity { get; init; } = Parity.None;
    public StopBits StopBits { get; init; } = StopBits.One;

    /// <summary>寄存器字节序（Modbus 标准为 ABCD，高字在前）</summary>
    public DataFormat DataFormat { get; init; } = DataFormat.ABCD;

    // ADR-003 P3-4：超时不再硬编码，由 ModbusRtuDriver 从连接参数 RequestTimeoutMs 透传
    /// <summary>通信接收超时（毫秒），默认 1000</summary>
    public int ReceiveTimeoutMs { get; init; } = 1000;

    /// <summary>串口读超时（毫秒），默认 1000</summary>
    public int ReadTimeoutMs { get; init; } = 1000;

    /// <summary>串口写超时（毫秒），默认 1000</summary>
    public int WriteTimeoutMs { get; init; } = 1000;
}

/// <summary>
/// 串口租约。持有共享的 ModbusRtu 实例与同端口读写闸门；
/// Dispose 后归还引用计数，最后一个租约释放时关闭串口。
/// </summary>
public sealed class SerialPortLease : IDisposable
{
    private readonly SerialPortManager _owner;
    private int _disposed;

    internal SerialPortLease(SerialPortManager owner, ModbusRtu rtu, SemaphoreSlim gate, SerialPortSettings settings)
    {
        _owner = owner;
        Rtu = rtu;
        Gate = gate;
        Settings = settings;
    }

    /// <summary>同端口共享的 ModbusRtu 客户端（已打开，帧级访问需先取 Gate）</summary>
    public ModbusRtu Rtu { get; }

    /// <summary>同端口所有设备共享的读写闸门，保证 Modbus 帧级串行</summary>
    public SemaphoreSlim Gate { get; }

    public SerialPortSettings Settings { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _owner.Release(this);
    }
}

/// <summary>串口状态快照（供管理面展示）</summary>
public sealed record SerialPortInfo
{
    public required string PortName { get; init; }
    public bool IsOpen { get; init; }
    public int LeaseCount { get; init; }
    public int BaudRate { get; init; }
    public int DataBits { get; init; }
    public string Parity { get; init; } = "None";
    public string StopBits { get; init; } = "One";
    public string DataFormat { get; init; } = "ABCD";
}

/// <summary>串口资源管理器：同一端口多设备共享、引用计数、帧级串行闸门</summary>
public interface ISerialPortManager
{
    /// <summary>获取串口租约；首次获取时打开串口。参数不一致或打开失败时抛异常。</summary>
    SerialPortLease Acquire(SerialPortSettings settings);

    /// <summary>列出系统可用串口（Windows COM 口 / Linux tty 设备）</summary>
    IReadOnlyList<string> GetAvailablePorts();

    /// <summary>当前串口占用状态</summary>
    IReadOnlyList<SerialPortInfo> GetStatus();
}

/// <inheritdoc cref="ISerialPortManager" />
public sealed class SerialPortManager : ISerialPortManager
{
    private readonly ILogger<SerialPortManager> _logger;
    private readonly object _lock = new();
    private readonly Dictionary<string, Entry> _ports = new(StringComparer.OrdinalIgnoreCase);

    private sealed class Entry
    {
        public required SerialPortSettings Settings { get; init; }
        public required ModbusRtu Rtu { get; init; }
        public required SemaphoreSlim Gate { get; init; }
        public int LeaseCount;
    }

    public SerialPortManager(ILogger<SerialPortManager> logger) => _logger = logger;

    public SerialPortLease Acquire(SerialPortSettings settings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settings.PortName);
        lock (_lock)
        {
            if (_ports.TryGetValue(settings.PortName, out var entry))
            {
                if (!SettingsEqual(entry.Settings, settings))
                    throw new InvalidOperationException(
                        $"串口 {settings.PortName} 已被其他设备以不同参数占用 " +
                        $"({entry.Settings.BaudRate},{entry.Settings.DataBits},{entry.Settings.Parity},{entry.Settings.StopBits})");

                entry.LeaseCount++;
                return new SerialPortLease(this, entry.Rtu, entry.Gate, entry.Settings);
            }

            var rtu = OpenRtu(settings);
            entry = new Entry { Settings = settings, Rtu = rtu, Gate = new SemaphoreSlim(1, 1), LeaseCount = 1 };
            _ports[settings.PortName] = entry;
            _logger.LogInformation("串口已打开: {Port} ({BaudRate},{DataBits},{Parity},{StopBits})",
                settings.PortName, settings.BaudRate, settings.DataBits, settings.Parity, settings.StopBits);
            return new SerialPortLease(this, rtu, entry.Gate, settings);
        }
    }

    private static ModbusRtu OpenRtu(SerialPortSettings settings)
    {
        var rtu = new ModbusRtu
        {
            ReceiveTimeOut = settings.ReceiveTimeoutMs,
            SleepTime = 5
        };

        rtu.SerialPortInni(sp =>
        {
            sp.PortName = settings.PortName;
            sp.BaudRate = settings.BaudRate;
            sp.DataBits = settings.DataBits;
            sp.Parity = settings.Parity;
            sp.StopBits = settings.StopBits;
            sp.ReadTimeout = settings.ReadTimeoutMs;
            sp.WriteTimeout = settings.WriteTimeoutMs;
        });

        rtu.DataFormat = settings.DataFormat;

        var r = rtu.Open();
        if (!r.IsSuccess)
        {
            rtu.Dispose();
            throw new IOException($"无法打开串口 {settings.PortName}: {r.Message}");
        }

        return rtu;
    }

    internal void Release(SerialPortLease lease)
    {
        lock (_lock)
        {
            if (!_ports.TryGetValue(lease.Settings.PortName, out var entry))
                return;

            entry.LeaseCount--;
            if (entry.LeaseCount > 0)
                return;

            _ports.Remove(lease.Settings.PortName);
            try { entry.Rtu.Close(); } catch { }
            entry.Rtu.Dispose();
            _logger.LogInformation("串口已释放: {Port}", lease.Settings.PortName);
        }
    }

    public IReadOnlyList<string> GetAvailablePorts()
    {
        if (OperatingSystem.IsWindows())
            return SerialPort.GetPortNames();

        var ports = new List<string>();
        if (!Directory.Exists("/dev"))
            return ports;

        foreach (var pattern in new[] { "ttyUSB*", "ttyACM*", "ttyS*", "cu.*" })
        {
            try
            {
                foreach (var f in Directory.GetFiles("/dev", pattern))
                    ports.Add(f);
            }
            catch { }
        }

        return ports;
    }

    public IReadOnlyList<SerialPortInfo> GetStatus()
    {
        lock (_lock)
        {
            return _ports.Values.Select(e => new SerialPortInfo
            {
                PortName = e.Settings.PortName,
                IsOpen = e.Rtu.IsOpen(),
                LeaseCount = e.LeaseCount,
                BaudRate = e.Settings.BaudRate,
                DataBits = e.Settings.DataBits,
                Parity = e.Settings.Parity.ToString(),
                StopBits = e.Settings.StopBits.ToString(),
                DataFormat = e.Settings.DataFormat.ToString()
            }).ToList();
        }
    }

    private static bool SettingsEqual(SerialPortSettings a, SerialPortSettings b) =>
        a.BaudRate == b.BaudRate && a.DataBits == b.DataBits && a.Parity == b.Parity &&
        a.StopBits == b.StopBits && a.DataFormat == b.DataFormat &&
        a.ReceiveTimeoutMs == b.ReceiveTimeoutMs && a.ReadTimeoutMs == b.ReadTimeoutMs &&
        a.WriteTimeoutMs == b.WriteTimeoutMs;
}
