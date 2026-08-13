using System.Collections;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using NitroGateway.Domain.Devices;

namespace NitroGateway.Desktop.ViewModels;

/// <summary>
/// 设备表单编辑模型（ADR-029 P3）。可变对象供 WPF 双向绑定；
/// 协议/传输方式切换时联动显隐对应字段（Modbus TCP/RTU、S7）。
/// 字段集与 Web DeviceForm.vue 对齐（含 ADR-024 P3-1 S7 参数 / P3-2 传输方式）。
/// </summary>
public sealed partial class DeviceEditor : ObservableObject, INotifyDataErrorInfo
{
    /// <summary>设备 ID：新建由调用方生成，编辑保留原 ID（RegisterAsync 按 ID upsert）</summary>
    /// <summary>RTU 允许的波特率枚举（ADR-037 S4）。</summary>
    private static readonly int[] ValidBaudRates = [9600, 19200, 38400, 57600, 115200];

    /// <summary>字段级错误表（属性名 -> 错误文案，由 Validate() 全量重算）。</summary>
    private readonly Dictionary<string, string> _errors = [];

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
    [ObservableProperty] private int _dataBits = 8;
    [ObservableProperty] private string _stopBits = "One";

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

    /// <summary>是否存在校验错误（ADR-037 S4）。</summary>
    public bool HasErrors => _errors.Count > 0;

    /// <summary>校验错误集合变更事件（WPF INotifyDataErrorInfo 订阅）。</summary>
    public event EventHandler<DataErrorsChangedEventArgs>? ErrorsChanged;

    /// <summary>取属性级错误；属性名为 null 时返回全部错误（WPF 约定）。</summary>
    public IEnumerable GetErrors(string? propertyName) =>
        propertyName is null
            ? _errors.Values.ToList()
            : _errors.TryGetValue(propertyName, out var error)
                ? new[] { error }
                : Array.Empty<string>();

    /// <summary>
    /// 全表单校验（ADR-037 S4）：重算全部字段错误并通知绑定。
    /// 规则：Name/Endpoint 非空；UnitId 1-247（仅 Modbus）；超时/重试/间隔大于 0；
    /// RTU 波特率与数据位必须为枚举值。返回是否通过（窗口保存前调用）。
    /// </summary>
    public bool Validate()
    {
        SetError(nameof(Name), string.IsNullOrWhiteSpace(Name) ? "设备名称不能为空" : null);
        SetError(nameof(Endpoint), string.IsNullOrWhiteSpace(Endpoint) ? "连接地址不能为空（TCP 填 IP:端口，RTU 填 COM 口）" : null);
        SetError(nameof(UnitId), IsModbus && UnitId is < 1 or > 247 ? "从站地址须在 1-247" : null);
        SetError(nameof(ConnectTimeoutMs), ConnectTimeoutMs <= 0 ? "连接超时须大于 0" : null);
        SetError(nameof(RequestTimeoutMs), RequestTimeoutMs <= 0 ? "请求超时须大于 0" : null);
        SetError(nameof(RetryCount), RetryCount <= 0 ? "重试次数须大于 0" : null);
        SetError(nameof(RetryIntervalMs), RetryIntervalMs <= 0 ? "重试间隔须大于 0" : null);
        SetError(nameof(BaudRate), IsRtu && !ValidBaudRates.Contains(BaudRate) ? "波特率须为 9600/19200/38400/57600/115200" : null);
        SetError(nameof(DataBits), IsRtu && DataBits is not (7 or 8) ? "数据位须为 7 或 8" : null);
        return !HasErrors;
    }

    /// <summary>更新单个属性错误并仅在该属性上发 ErrorsChanged。</summary>
    private void SetError(string propertyName, string? error)
    {
        var exists = _errors.TryGetValue(propertyName, out var current);
        if (error is null)
        {
            if (exists)
            {
                _errors.Remove(propertyName);
                ErrorsChanged?.Invoke(this, new DataErrorsChangedEventArgs(propertyName));
            }
            return;
        }

        if (exists && current == error)
            return;
        _errors[propertyName] = error;
        ErrorsChanged?.Invoke(this, new DataErrorsChangedEventArgs(propertyName));
    }

    // 字段变更即重算全表单（字段少、全量校验开销可忽略；协议切换联动边界同步生效）
    partial void OnNameChanged(string value) => Validate();
    partial void OnEndpointChanged(string value) => Validate();
    partial void OnProtocolNameChanged(string value) => Validate();
    partial void OnDialectChanged(string value) => Validate();
    partial void OnUnitIdChanged(int value) => Validate();
    partial void OnConnectTimeoutMsChanged(int value) => Validate();
    partial void OnRequestTimeoutMsChanged(int value) => Validate();
    partial void OnRetryCountChanged(int value) => Validate();
    partial void OnRetryIntervalMsChanged(int value) => Validate();
    partial void OnBaudRateChanged(int value) => Validate();
    partial void OnDataBitsChanged(int value) => Validate();

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
                parameters["DataBits"] = DataBits;
                parameters["StopBits"] = StopBits;
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
            // 修复 ADR-036 后遗留：早期 ComboBoxItem 绑定把选中项 ToString 存成
            // "System.Windows.Controls.ComboBoxItem: Modbus"，回填时归一化为纯值，
            // 保证旧脏数据重编辑后保存即可恢复采集。
            ProtocolName = Normalize(device.Protocol.Name),
            Dialect = Normalize(string.IsNullOrEmpty(device.Protocol.Dialect) ? "TCP" : device.Protocol.Dialect),
            Status = device.Status,
            Endpoint = device.Connection.Endpoint,
            ConnectTimeoutMs = device.Connection.ConnectTimeoutMs,
            RequestTimeoutMs = device.Connection.RequestTimeoutMs,
            RetryCount = device.Connection.RetryCount,
            RetryIntervalMs = device.Connection.RetryIntervalMs,
            UnitId = ToInt(p, "UnitId", 1),
            DataFormat = Normalize(p.GetValueOrDefault("DataFormat")?.ToString() ?? "ABCD"),
            BaudRate = ToInt(p, "BaudRate", 9600),
            Parity = Normalize(p.GetValueOrDefault("Parity")?.ToString() ?? "None"),
            DataBits = ToInt(p, "DataBits", 8),
            StopBits = Normalize(p.GetValueOrDefault("StopBits")?.ToString() ?? "One"),
            Rack = ToInt(p, "Rack", 0),
            Slot = ToInt(p, "Slot", 1),
            CpuType = Normalize(p.GetValueOrDefault("CpuType")?.ToString() ?? "S-1200"),
            PingAddress = p.GetValueOrDefault("PingAddress")?.ToString() ?? "DB1.DBW0"
        };
    }

    /// <summary>
    /// 归一化下拉框旧脏值：剥离 WPF ComboBoxItem.ToString() 前缀（ADR-036 绑定修复），
    /// 无前缀或空值原样返回。
    /// </summary>
    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return value ?? "";
        const string ComboBoxItemPrefix = "System.Windows.Controls.ComboBoxItem: ";
        return value.StartsWith(ComboBoxItemPrefix, StringComparison.Ordinal)
            ? value[ComboBoxItemPrefix.Length..]
            : value;
    }

    private static int ToInt(IReadOnlyDictionary<string, object> p, string key, int fallback)
        => p.TryGetValue(key, out var value) && int.TryParse(value?.ToString(), out var n) ? n : fallback;
}
