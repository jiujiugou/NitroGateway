using NitroGateway.Desktop.ViewModels;
using NitroGateway.Domain.Devices;
using Xunit;

namespace NitroGateway.UnitTests;

/// <summary>
/// ADR-029 P3：设备表单模型——协议/传输方式联动字段映射：
/// Modbus TCP（UnitId/DataFormat）、Modbus RTU（+Transport/BaudRate/Parity）、
/// S7（Rack/Slot/CpuType/PingAddress），与 Web DeviceForm.vue 对齐。
/// </summary>
public sealed class DeviceEditorTests
{
    [Fact]
    public void ToDevice_modbusTcp_maps_unit_and_data_format()
    {
        var editor = new DeviceEditor
        {
            Name = "PLC-1",
            Endpoint = "192.168.1.10:502",
            ProtocolName = "Modbus",
            Dialect = "TCP",
            UnitId = 7,
            DataFormat = "CDAB"
        };

        var device = editor.ToDevice();

        Assert.Equal("Modbus", device.Protocol.Name);
        Assert.Equal("TCP", device.Protocol.Dialect);
        Assert.Equal(7, (int)device.Connection.Parameters["UnitId"]);
        Assert.Equal("CDAB", device.Connection.Parameters["DataFormat"]);
        Assert.False(device.Connection.Parameters.ContainsKey("Transport"));
    }

    [Fact]
    public void ToDevice_modbusRtu_adds_transport_baud_and_parity()
    {
        var editor = new DeviceEditor
        {
            ProtocolName = "Modbus",
            Dialect = "RTU",
            BaudRate = 19200,
            Parity = "Even"
        };

        var device = editor.ToDevice();

        Assert.Equal("RTU", device.Connection.Parameters["Transport"]);
        Assert.Equal(19200, (int)device.Connection.Parameters["BaudRate"]);
        Assert.Equal("Even", device.Connection.Parameters["Parity"]);
    }

    [Fact]
    public void ToDevice_s7_maps_rack_slot_cpu_and_ping()
    {
        var editor = new DeviceEditor
        {
            ProtocolName = "S7",
            Dialect = "TCP",
            Rack = 0,
            Slot = 2,
            CpuType = "S-1500",
            PingAddress = "DB2.DBW4"
        };

        var device = editor.ToDevice();

        Assert.Equal(0, (int)device.Connection.Parameters["Rack"]);
        Assert.Equal(2, (int)device.Connection.Parameters["Slot"]);
        Assert.Equal("S-1500", device.Connection.Parameters["CpuType"]);
        Assert.Equal("DB2.DBW4", device.Connection.Parameters["PingAddress"]);
        Assert.False(device.Connection.Parameters.ContainsKey("UnitId"));
    }

    [Fact]
    public void FromDevice_roundtrip_preserves_s7_parameters()
    {
        var device = new Device
        {
            Id = Guid.NewGuid(),
            Name = "S7 设备",
            Protocol = new ProtocolIdentifier { Name = "S7", Dialect = "TCP" },
            Connection = new DeviceConnection
            {
                Endpoint = "192.168.1.20:102",
                ConnectTimeoutMs = 5000,
                RequestTimeoutMs = 8000,
                RetryCount = 5,
                RetryIntervalMs = 2000,
                Parameters = new Dictionary<string, object>
                {
                    ["Rack"] = 0, ["Slot"] = 3, ["CpuType"] = "S-400", ["PingAddress"] = "DB3.DBD8"
                }
            }
        };

        var editor = DeviceEditor.FromDevice(device);
        var roundtrip = editor.ToDevice();

        Assert.Equal(device.Id, roundtrip.Id);
        Assert.Equal(device.Name, roundtrip.Name);
        Assert.Equal("S7", roundtrip.Protocol.Name);
        Assert.Equal(3, (int)roundtrip.Connection.Parameters["Slot"]);
        Assert.Equal("S-400", roundtrip.Connection.Parameters["CpuType"]);
        Assert.Equal("DB3.DBD8", roundtrip.Connection.Parameters["PingAddress"]);
        Assert.Equal(5000, roundtrip.Connection.ConnectTimeoutMs);
        Assert.Equal(5, roundtrip.Connection.RetryCount);
    }

    [Fact]
    public void IsRtu_only_true_for_modbus_rtu()
    {
        Assert.True(new DeviceEditor { ProtocolName = "Modbus", Dialect = "RTU" }.IsRtu);
        Assert.False(new DeviceEditor { ProtocolName = "Modbus", Dialect = "TCP" }.IsRtu);
        Assert.False(new DeviceEditor { ProtocolName = "S7", Dialect = "RTU" }.IsRtu);
    }
}
