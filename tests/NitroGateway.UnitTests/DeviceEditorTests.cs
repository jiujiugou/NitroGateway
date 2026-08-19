using NitroGateway.Desktop.ViewModels;
using NitroGateway.Desktop.Services.Connectivity;
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

    [Fact]
    public void ToDevice_modbusRtu_maps_data_bits_and_stop_bits()
    {
        var editor = new DeviceEditor
        {
            ProtocolName = "Modbus",
            Dialect = "RTU",
            DataBits = 7,
            StopBits = "Two"
        };

        var device = editor.ToDevice();

        Assert.Equal(7, (int)device.Connection.Parameters["DataBits"]);
        Assert.Equal("Two", device.Connection.Parameters["StopBits"]);
    }

    [Fact]
    public void FromDevice_roundtrip_preserves_rtu_serial_parameters()
    {
        var device = new Device
        {
            Id = Guid.NewGuid(),
            Name = "RTU 设备",
            Protocol = new ProtocolIdentifier { Name = "Modbus", Dialect = "RTU" },
            Connection = new DeviceConnection
            {
                Endpoint = "COM3",
                Parameters = new Dictionary<string, object>
                {
                    ["UnitId"] = 5, ["DataFormat"] = "CDAB", ["BaudRate"] = 19200,
                    ["Parity"] = "Even", ["DataBits"] = 7, ["StopBits"] = "Two", ["Transport"] = "RTU"
                }
            }
        };

        var editor = DeviceEditor.FromDevice(device);
        var roundtrip = editor.ToDevice();

        Assert.Equal("RTU", roundtrip.Connection.Parameters["Transport"]);
        Assert.Equal(5, (int)roundtrip.Connection.Parameters["UnitId"]);
        Assert.Equal(19200, (int)roundtrip.Connection.Parameters["BaudRate"]);
        Assert.Equal("Even", roundtrip.Connection.Parameters["Parity"]);
        Assert.Equal(7, (int)roundtrip.Connection.Parameters["DataBits"]);
        Assert.Equal("Two", roundtrip.Connection.Parameters["StopBits"]);
    }

    [Fact]
    public void FromDevice_normalizes_comboItem_prefixed_values()
    {
        // ADR-036 绑定修复前，下拉框把选中项 ToString 存为
        // "System.Windows.Controls.ComboBoxItem: Modbus"，回填必须归一化。
        var device = new Device
        {
            Name = "坏协议设备",
            Protocol = new ProtocolIdentifier
            {
                Name = "System.Windows.Controls.ComboBoxItem: Modbus",
                Dialect = "System.Windows.Controls.ComboBoxItem: RTU"
            },
            Connection = new DeviceConnection
            {
                Endpoint = "COM3",
                Parameters = new Dictionary<string, object>
                {
                    ["DataFormat"] = "System.Windows.Controls.ComboBoxItem: CDAB",
                    ["Parity"] = "System.Windows.Controls.ComboBoxItem: Even",
                    ["StopBits"] = "System.Windows.Controls.ComboBoxItem: Two",
                    ["CpuType"] = "System.Windows.Controls.ComboBoxItem: S-400"
                }
            }
        };

        var editor = DeviceEditor.FromDevice(device);

        Assert.Equal("Modbus", editor.ProtocolName);
        Assert.Equal("RTU", editor.Dialect);
        Assert.Equal("CDAB", editor.DataFormat);
        Assert.Equal("Even", editor.Parity);
        Assert.Equal("Two", editor.StopBits);
        Assert.Equal("S-400", editor.CpuType);

        // 归一化后保存即写回纯值，采集引擎可识别协议。
        var roundtrip = editor.ToDevice();
        Assert.Equal("Modbus", roundtrip.Protocol.Name);
        Assert.Equal("RTU", roundtrip.Protocol.Dialect);
    }

    // ===== ADR-037 S4：字段级校验 =====

    [Fact]
    public void Validate_rejects_empty_name_and_endpoint()
    {
        var editor = new DeviceEditor { Name = " ", Endpoint = "" };

        Assert.False(editor.Validate());
        Assert.True(editor.HasErrors);
        Assert.Contains("名称", Assert.Single(editor.GetErrors(nameof(DeviceEditor.Name)).Cast<string>()));
        Assert.Contains("地址", Assert.Single(editor.GetErrors(nameof(DeviceEditor.Endpoint)).Cast<string>()));
    }

    [Fact]
    public void Validate_rejects_unit_id_out_of_range_only_for_modbus()
    {
        var modbus = new DeviceEditor { Name = "PLC", Endpoint = "192.168.1.1:502", ProtocolName = "Modbus", UnitId = 0 };

        Assert.False(modbus.Validate());
        Assert.Contains("1-247", Assert.Single(modbus.GetErrors(nameof(DeviceEditor.UnitId)).Cast<string>()));

        var s7 = new DeviceEditor { Name = "PLC", Endpoint = "192.168.1.1:102", ProtocolName = "S7", UnitId = 0 };
        Assert.True(s7.Validate());
    }

    [Fact]
    public void Validate_rejects_nonpositive_timeouts_retries_intervals()
    {
        var editor = new DeviceEditor
        {
            Name = "PLC",
            Endpoint = "192.168.1.1:502",
            ConnectTimeoutMs = 0,
            RequestTimeoutMs = -1,
            RetryCount = 0,
            RetryIntervalMs = -5
        };

        Assert.False(editor.Validate());
        foreach (var property in new[]
        {
            nameof(DeviceEditor.ConnectTimeoutMs), nameof(DeviceEditor.RequestTimeoutMs),
            nameof(DeviceEditor.RetryCount), nameof(DeviceEditor.RetryIntervalMs)
        })
        {
            Assert.NotEmpty(editor.GetErrors(property).Cast<string>());
        }
    }

    [Fact]
    public void Validate_rejects_rtu_baud_and_data_bits_outside_enum()
    {
        var editor = new DeviceEditor
        {
            Name = "RTU-1",
            Endpoint = "COM3",
            ProtocolName = "Modbus",
            Dialect = "RTU",
            BaudRate = 4800,
            DataBits = 6
        };

        Assert.False(editor.Validate());
        Assert.Contains("波特率", Assert.Single(editor.GetErrors(nameof(DeviceEditor.BaudRate)).Cast<string>()));
        Assert.Contains("数据位", Assert.Single(editor.GetErrors(nameof(DeviceEditor.DataBits)).Cast<string>()));
    }

    [Fact]
    public void Validate_rtu_rules_not_applied_for_tcp()
    {
        var editor = new DeviceEditor
        {
            Name = "TCP-1",
            Endpoint = "192.168.1.1:502",
            ProtocolName = "Modbus",
            Dialect = "TCP",
            BaudRate = 4800,
            DataBits = 6
        };

        Assert.True(editor.Validate());
    }

    [Fact]
    public void Validate_errors_clear_when_field_fixed()
    {
        var editor = new DeviceEditor { Name = "", Endpoint = "" };
        Assert.False(editor.Validate());

        editor.Name = "PLC";
        editor.Endpoint = "192.168.1.1:502";

        Assert.True(editor.Validate());
        Assert.False(editor.HasErrors);
    }

    // ===== ADR-044：连接测试命令（测试动作收敛为命令 + 结果属性，窗口不再直接操控件） =====

    [Fact]
    public void IsTestEnabled_false_without_tester()
    {
        var editor = new DeviceEditor();

        Assert.False(editor.IsTestEnabled);
        Assert.False(editor.TestConnectionCommand.CanExecute(null));
    }

    [Fact]
    public async Task TestConnection_calls_tester_and_writes_success_text()
    {
        var tester = new StubConnectionTester { Result = new ConnectionTestResult(true, 12, null, "ok") };
        var editor = new DeviceEditor { Name = "PLC-1", ConnectionTester = tester };

        Assert.True(editor.IsTestEnabled);
        await editor.TestConnectionCommand.ExecuteAsync(null);

        var tested = Assert.Single(tester.Calls);
        Assert.Equal("PLC-1", tested.Name);
        Assert.Contains("连接成功", editor.TestResultText);
        Assert.Contains("12", editor.TestResultText);
        Assert.False(editor.IsTestingConnection);
    }

    [Fact]
    public async Task TestConnection_failure_writes_error_text()
    {
        var tester = new StubConnectionTester { Result = new ConnectionTestResult(false, 0, "从站无响应") };
        var editor = new DeviceEditor { Name = "PLC-1", ConnectionTester = tester };

        await editor.TestConnectionCommand.ExecuteAsync(null);

        Assert.Contains("连接失败", editor.TestResultText);
        Assert.Contains("从站无响应", editor.TestResultText);
        Assert.False(editor.IsTestingConnection);
    }

    [Fact]
    public async Task TestConnection_marks_testing_while_running()
    {
        var gate = new TaskCompletionSource<ConnectionTestResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tester = new StubConnectionTester { Gate = gate.Task };
        var editor = new DeviceEditor { Name = "PLC-1", ConnectionTester = tester };

        // 测试动作挂起：命令先开始执行，停在 TestAsync 的 Gate 上
        var running = editor.TestConnectionCommand.ExecuteAsync(null);

        // 挂起期间按钮禁用
        Assert.True(editor.IsTestingConnection);
        Assert.False(editor.IsTestEnabled);
        Assert.Contains("测试中", editor.TestResultText);

        gate.SetResult(new ConnectionTestResult(true, 3, null, "ok"));
        await running;

        Assert.False(editor.IsTestingConnection);
        Assert.True(editor.IsTestEnabled);
        Assert.Contains("连接成功", editor.TestResultText);
    }
}
