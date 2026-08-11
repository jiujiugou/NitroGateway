using CommunityToolkit.Mvvm.ComponentModel;
using NitroGateway.Domain.Devices;

namespace NitroGateway.Desktop.ViewModels;

/// <summary>
/// 设备表单编辑模型（ADR-029 P3）。可变对象供 WPF 双向绑定；
/// 协议/传输方式切换时联动显隐对应字段（Modbus TCP/RTU、S7）。
/// 字段集与 Web DeviceForm.vue 对齐（含 ADR-024 P3-1 S7 参数 / P3-2 传输方式）。
/// </summary>
public sealed partial class DeviceEditor : ObservableObject
{
    /// <summary>设备 ID：新建由调用方生成，编辑保留原 ID（RegisterAsync 按 ID upsert）</summary>
    public Guid Id { get; set; }

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string? _description;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModbus))]
    [NotifyPropertyChangedFor(nameof(IsS7))]
    [NotifyPropertyChangedFor(nameof(IsRtu))]
    private string _protocolName = "Modbus";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRtu))]
    private string _dialect = "TCP";

    /// <summary>新建默认 Unknown，由 HealthMonitor 驱动状态（不手填 Online 伪状态）</summary>
    [ObservableProperty] private DeviceStatus _status = DeviceStatus.Unknown;

    /// <summary>连接地址：Modbus TCP "192.168.1.100:502"、RTU 串口 "COM3"、S7 "192.168.1.100:102"</summary>
    [ObservableProperty] private string _endpoint = "";

    // ── Modbus ──
    [ObservableProperty] private int _unitId = 1;
    [ObservableProperty] private string _dataFormat = "ABCD";
    [ObservableProperty] private int _baudRate = 9600;
    [ObservableProperty] private string _parity = "None";

    // ── S7（ADR-024 P3-1：不落库则后端只能用默认值，S7-300/400 必连不上）──
    [ObservableProperty] private int _rack;
    [ObservableProperty] private int _slot = 1;
    [ObservableProperty] private string _cpuType = "S-1200";
    [ObservableProperty] private string _pingAddress = "DB1.DBW0";

    // ── 连接参数 ──
    [ObservableProperty] private int _connectTimeoutMs = 3000;
    [ObservableProperty] private int _requestTimeoutMs = 5000;
    [ObservableProperty] private int _retryCount = 3;
    [ObservableProperty] private int _retryIntervalMs = 1000;

    public bool IsModbus => ProtocolName == "Modbus";
    public bool IsS7 => ProtocolName == "S7";
    public bool IsRtu => IsModbus && Dialect == "RTU";

    /// <summary>由表单构建设备实体（协议参数按当前选择写入 Parameters）</summary>
    public Device ToDevice()
    {
        var parameters = new Dictionary<string, object>();
        if (IsModbus)
        {
            parameters["UnitId"] = UnitId;
            parameters["DataFormat"] = DataFormat;
            if (IsRtu)
            {
                parameters["Transport"] = "RTU";
                parameters["BaudRate"] = BaudRate;
                parameters["Parity"] = Parity;
            }
        }
        else if (IsS7)
        {
            parameters["Rack"] = Rack;
            parameters["Slot"] = Slot;
            parameters["CpuType"] = CpuType;
            parameters["PingAddress"] = PingAddress;
        }

        return new Device
        {
            Id = Id,
            Name = Name,
            Description = Description,
            Protocol = new ProtocolIdentifier { Name = ProtocolName, Dialect = Dialect },
            Connection = new DeviceConnection
            {
                Endpoint = Endpoint,
                ConnectTimeoutMs = ConnectTimeoutMs,
                RequestTimeoutMs = RequestTimeoutMs,
                RetryCount = RetryCount,
                RetryIntervalMs = RetryIntervalMs,
                Parameters = parameters
            },
            Status = Status
        };
    }

    /// <summary>由已有设备回填表单（编辑场景）</summary>
    public static DeviceEditor FromDevice(Device device)
    {
        var p = device.Connection.Parameters;
        return new DeviceEditor
        {
            Id = device.Id,
            Name = device.Name,
            Description = device.Description,
            ProtocolName = device.Protocol.Name,
            Dialect = string.IsNullOrEmpty(device.Protocol.Dialect) ? "TCP" : device.Protocol.Dialect,
            Status = device.Status,
            Endpoint = device.Connection.Endpoint,
            ConnectTimeoutMs = device.Connection.ConnectTimeoutMs,
            RequestTimeoutMs = device.Connection.RequestTimeoutMs,
            RetryCount = device.Connection.RetryCount,
            RetryIntervalMs = device.Connection.RetryIntervalMs,
            UnitId = ToInt(p, "UnitId", 1),
            DataFormat = p.GetValueOrDefault("DataFormat")?.ToString() ?? "ABCD",
            BaudRate = ToInt(p, "BaudRate", 9600),
            Parity = p.GetValueOrDefault("Parity")?.ToString() ?? "None",
            Rack = ToInt(p, "Rack", 0),
            Slot = ToInt(p, "Slot", 1),
            CpuType = p.GetValueOrDefault("CpuType")?.ToString() ?? "S-1200",
            PingAddress = p.GetValueOrDefault("PingAddress")?.ToString() ?? "DB1.DBW0"
        };
    }

    private static int ToInt(IReadOnlyDictionary<string, object> p, string key, int fallback)
        => p.TryGetValue(key, out var value) && int.TryParse(value?.ToString(), out var n) ? n : fallback;
}
