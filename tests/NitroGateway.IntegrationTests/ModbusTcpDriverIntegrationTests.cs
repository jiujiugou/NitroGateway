using HslCommunication.Core;
using HslCommunication.ModBus;
using Microsoft.Extensions.Logging.Abstractions;
using NitroGateway.Domain.Devices;
using NitroGateway.Protocols.Modbus;
using Xunit;

namespace NitroGateway.IntegrationTests;

/// <summary>
/// ADR-003 P1-1/P1-2：基于 HslCommunication ModbusTcpServer 的真实 TCP 回环测试。
/// P1-1：类型组内非连续点位必须切段，否则连读会把间隔寄存器误读成后序点位；
/// P1-2：各 DataType 写类型映射后能写读回环。
/// </summary>
public class ModbusTcpDriverIntegrationTests
{
    [Fact]
    public async Task ReadBatchAsync_NonContiguousSameType_SplitsSegments()
    {
        using var scope = new ModbusServerScope();
        var driver = CreateDriver(scope);
        var connect = await driver.ConnectAsync();
        Assert.True(connect.IsSuccess, connect.Error?.Message);

        try
        {
            // 寄存器布局（HoldingRegister，offset 从 0 起）：
            //   A Float  @ 40001 (offset 0, 2 寄存器)
            //   B Int16  @ 40003 (offset 2, 1 寄存器)
            //   C Float  @ 40004 (offset 3, 2 寄存器)
            //   D Float  @ 40006 (offset 5, 2 寄存器) —— C/D 同类型且连续，应合并为一段
            var pointA = Point("A", "40001", DataType.Float);
            var pointB = Point("B", "40003", DataType.Int16);
            var pointC = Point("C", "40004", DataType.Float);
            var pointD = Point("D", "40006", DataType.Float);

            // 用直连客户端写原始寄存器，模拟 PLC 真实寄存器布局
            using var client = new ModbusTcpNet
            {
                IpAddress = "127.0.0.1",
                Port = scope.Port,
                DataFormat = DataFormat.ABCD,
                ReceiveTimeOut = 3000
            };
            Assert.True((await client.ConnectServerAsync()).IsSuccess);
            Assert.True((await client.WriteAsync("0", 11.5f)).IsSuccess);      // A
            Assert.True((await client.WriteAsync("2", (short)-7)).IsSuccess);  // B
            Assert.True((await client.WriteAsync("3", 22.25f)).IsSuccess);     // C
            Assert.True((await client.WriteAsync("5", 33.75f)).IsSuccess);     // D
            client.ConnectClose();

            var result = await driver.ReadBatchAsync([pointA, pointB, pointC, pointD], CancellationToken.None);

            Assert.True(result.IsSuccess, result.Error?.Message);
            Assert.Equal(4, result.Value!.Count);
            var values = result.Value.ToDictionary(v => v.Point.Name, v => v.Value);
            Assert.Equal(11.5f, (float)values["A"]!);
            Assert.Equal((short)-7, (short)values["B"]!);
            Assert.Equal(22.25f, (float)values["C"]!);
            Assert.Equal(33.75f, (float)values["D"]!);
        }
        finally
        {
            await driver.DisconnectAsync();
        }
    }

    [Fact]
    public async Task WriteAsync_AllTypes_Roundtrips()
    {
        using var scope = new ModbusServerScope();
        var driver = CreateDriver(scope);
        var connect = await driver.ConnectAsync();
        Assert.True(connect.IsSuccess, connect.Error?.Message);

        try
        {
            var cases = new (DataType Type, object Value, string Address)[]
            {
                (DataType.Float, 12.5f, "40101"),
                (DataType.Double, 123456.789, "40111"),
                (DataType.Int16, (short)-123, "40121"),
                (DataType.UInt16, (ushort)60000, "40131"),
                (DataType.Int32, 123456789, "40141"),
                (DataType.UInt32, 3456789012u, "40151"),
                (DataType.Int64, -987654321012345L, "40161"),
                (DataType.UInt64, 9876543210123456789UL, "40171"),
                (DataType.Byte, (byte)200, "40181"),
                (DataType.Bool, true, "40191"),
                (DataType.String, "hello", "40201")
            };

            foreach (var (type, value, address) in cases)
            {
                var point = Point(type.ToString(), address, type);
                var w = await driver.WriteAsync(point, value, CancellationToken.None);
                Assert.True(w.IsSuccess, $"写入 {type} 失败: {w.Error?.Message}");

                var r = await driver.ReadAsync(point, CancellationToken.None);
                Assert.True(r.IsSuccess, $"读取 {type} 失败: {r.Error?.Message}");

                var actual = r.Value!.Value;
                switch (type)
                {
                    case DataType.Float:   Assert.Equal((float)value, (float)actual!); break;
                    case DataType.Double:  Assert.Equal((double)value, (double)actual!); break;
                    case DataType.Int16:   Assert.Equal((short)value, (short)actual!); break;
                    case DataType.UInt16:  Assert.Equal((ushort)value, (ushort)actual!); break;
                    case DataType.Int32:   Assert.Equal((int)value, (int)actual!); break;
                    case DataType.UInt32:  Assert.Equal((uint)value, (uint)actual!); break;
                    case DataType.Int64:   Assert.Equal((long)value, (long)actual!); break;
                    case DataType.UInt64:  Assert.Equal((ulong)value, (ulong)actual!); break;
                    case DataType.Byte:    Assert.Equal((byte)value, (byte)actual!); break;
                    case DataType.Bool:    Assert.Equal((bool)value, (bool)actual!); break;
                    case DataType.String:  Assert.Equal((string)value, ((string)actual!).TrimEnd('\0')); break;
                }
            }
        }
        finally
        {
            await driver.DisconnectAsync();
        }
    }

    // ── Helpers ──

    private static DevicePoint Point(string name, string address, DataType type) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Address = address,
        DataType = type
    };

    private static ModbusTcpDriver CreateDriver(ModbusServerScope scope) => new(
        new DeviceConnection
        {
            Endpoint = $"127.0.0.1:{scope.Port}",
            ConnectTimeoutMs = 3000,
            RequestTimeoutMs = 3000
        },
        NullLogger<ModbusTcpDriver>.Instance);

    private sealed class ModbusServerScope : IDisposable
    {
        public ModbusTcpServer Server { get; }
        public int Port { get; }

        public ModbusServerScope()
        {
            Port = FindFreePort();
            Server = new ModbusTcpServer { DataFormat = DataFormat.ABCD };
            Server.ServerStart(Port, 0);
            Assert.True(Server.IsStarted, "ModbusTcpServer 启动失败");
        }

        public void Dispose() => Server.ServerClose();

        private static int FindFreePort()
        {
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
    }
}
