using System.Net;
using System.Net.Sockets;

namespace NitroGateway.Host;

/// <summary>
/// 模拟 Modbus TCP 设备。监听端口，自动波动传感器值。
/// 无需 ModbusPal。线程安全。
/// </summary>
public sealed class FakeModbusDevice : IAsyncDisposable
{
    private readonly int _port;
    private readonly byte _unitId;
    private readonly List<SimSensor> _sensors = new();
    private readonly ushort[] _registers = new ushort[256]; // 最多 256 个寄存器
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    public FakeModbusDevice(int port = 502, byte unitId = 1)
    {
        _port = port;
        _unitId = unitId;
    }

    /// <summary>添加一个模拟传感器</summary>
    /// <param name="offset">寄存器偏移（0 = 40001）</param>
    /// <param name="count">寄存器数（1=Int16, 2=Float/Int32）</param>
    public void AddSensor(ushort offset, ushort count, string name, double initValue,
        double min, double max, double noise)
    {
        _sensors.Add(new SimSensor(offset, count, name, initValue, min, max, noise));
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _listener = new TcpListener(IPAddress.Loopback, _port);
        _listener.Start();

        Console.WriteLine($"  [模拟PLC] 已启动 tcp://127.0.0.1:{_port} (UnitId={_unitId})");
        foreach (var s in _sensors)
            Console.WriteLine($"  [模拟PLC]   {s}");

        try
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                _ = HandleAsync(client, _cts.Token);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            var stream = client.GetStream();
            var buf = new byte[256];

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var n = await stream.ReadAsync(buf, 0, 12, ct);
                    if (n < 12) break;

                    var txId = (ushort)((buf[0] << 8) | buf[1]);
                    var func = buf[7];
                    var addr = (ushort)((buf[8] << 8) | buf[9]);
                    var qty = (ushort)((buf[10] << 8) | buf[11]);

                    byte[] resp;
                    if (func == 0x03)      resp = ReadRegisters(txId, addr, qty);
                    else if (func == 0x01) resp = ReadCoils(txId, addr, qty);
                    else                   resp = ErrorResponse(txId, func, 0x01);

                    await stream.WriteAsync(resp, ct);
                }
            }
            catch (IOException) { }
        }
    }

    private void TickSensors()
    {
        lock (_registers)
        {
            foreach (var s in _sensors)
            {
                s.Value += (Random.Shared.NextDouble() - 0.5) * s.Noise;
                s.Value = Math.Clamp(s.Value, s.Min, s.Max);
                s.WriteTo(_registers);
            }
        }
    }

    private byte[] ReadRegisters(ushort txId, ushort addr, ushort qty)
    {
        TickSensors();

        var byteCount = (byte)(qty * 2);
        var resp = new byte[9 + byteCount];
        WriteMbap(resp, txId, (ushort)(3 + byteCount));
        resp[7] = 0x03;
        resp[8] = byteCount;

        lock (_registers)
        {
            for (int i = 0; i < qty && addr + i < _registers.Length; i++)
            {
                var v = _registers[addr + i];
                resp[9 + i * 2] = (byte)(v >> 8);
                resp[9 + i * 2 + 1] = (byte)(v & 0xFF);
            }
        }
        return resp;
    }

    private byte[] ReadCoils(ushort txId, ushort addr, ushort qty)
    {
        var byteCount = (byte)Math.Ceiling(qty / 8.0);
        var resp = new byte[9 + byteCount];
        WriteMbap(resp, txId, (ushort)(3 + byteCount));
        resp[7] = 0x01;
        resp[8] = byteCount;
        resp[9] = 0x01; // 位 0 = 1 (ON)
        return resp;
    }

    private static byte[] ErrorResponse(ushort txId, byte func, byte code)
    {
        var resp = new byte[9];
        WriteMbap(resp, txId, 3);
        resp[7] = (byte)(func | 0x80);
        resp[8] = code;
        return resp;
    }

    private static void WriteMbap(byte[] resp, ushort txId, ushort length)
    {
        resp[0] = (byte)(txId >> 8);
        resp[1] = (byte)(txId & 0xFF);
        resp[5] = (byte)(length & 0xFF);
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        _listener?.Stop();
    }

    // ---- 内部 ----

    private sealed class SimSensor
    {
        private readonly ushort _offset;
        private readonly ushort _count;
        private readonly string _name;
        public double Value;
        public double Min, Max, Noise;

        public SimSensor(ushort offset, ushort count, string name, double init,
            double min, double max, double noise)
        {
            _offset = offset; _count = count; _name = name; Value = init;
            Min = min; Max = max; Noise = noise;
        }

        public void WriteTo(ushort[] regs)
        {
            if (_count == 1)
            {
                regs[_offset] = (ushort)Math.Round(Value);
            }
            else if (_count == 2)
            {
                var bytes = BitConverter.GetBytes((float)Value);
                if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
                regs[_offset] = (ushort)((bytes[0] << 8) | bytes[1]);
                regs[_offset + 1] = (ushort)((bytes[2] << 8) | bytes[3]);
            }
        }

        public override string ToString() =>
            $"{_name} @ 4000{_offset} ({_count} reg), init={Value:F1}";
    }
}
