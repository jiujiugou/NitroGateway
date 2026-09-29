using System.Collections;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NitroGateway.Desktop.Services.Connectivity;
using NitroGateway.Domain.Devices;

namespace NitroGateway.Desktop.ViewModels;

public sealed partial class DeviceEditor : ObservableObject, INotifyDataErrorInfo
{
    private static readonly int[] ValidBaudRates = [9600, 19200, 38400, 57600, 115200];

    /// <summary>字段级错误表（属性名 -> 错误文案，由 Validate() 全量重算）。</summary>
    private readonly Dictionary<string, string> _errors = [];

    /// <summary>设备 ID：新建由调用方生成，编辑保留原 ID（RegisterAsync 按 ID upsert）</summary>
    public Guid Id { get; set; }

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string? _description;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModbus))]
    [NotifyPropertyChangedFor(nameof(IsS7))]
    [NotifyPropertyChangedFor(nameof(IsOpcUa))]
    [NotifyPropertyChangedFor(nameof(IsRtu))]
    [NotifyPropertyChangedFor(nameof(IsDialectEditable))]
    [NotifyPropertyChangedFor(nameof(DialectItems))]
    [NotifyPropertyChangedFor(nameof(EndpointLabel))]
    private string _protocolName = "Modbus";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRtu))]
    private string _dialect = "TCP";

    /// <summary>新建默认 Unknown，由 HealthMonitor 驱动状态（不手填 Online 伪状态）</summary>
    [ObservableProperty] private DeviceStatus _status = DeviceStatus.Unknown;

    /// <summary>
    /// 连接测试服务（ADR-044，由 DeviceDialogService.EditDevice 注入；null 时测试按钮禁用）。
    /// 把测试动作收敛为命令 + 状态绑定，窗口 code-behind 不再直接操控件。
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestConnectionCommand))]
    private IDeviceConnectionTester? _connectionTester;

    /// <summary>测试中标志：测试期间禁用按钮并阻止并发点击。</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestConnectionCommand))]
    private bool _isTestingConnection;

    /// <summary>连接测试结果文案（窗口底部内联展示）。</summary>
    [ObservableProperty]
    private string _testResultText = "";

    /// <summary>
    /// 连接地址：Modbus TCP "127.0.0.1:502"、RTU 串口 "COM3"、S7 "127.0.0.1:102"、
    /// OPC UA "opc.tcp://127.0.0.1:4840"。默认取 Modbus 端点（对齐 web DeviceForm.vue），
    /// 切协议时 <see cref="OnProtocolNameChanged"/> 自动换成对应协议默认端点。
    /// </summary>
    [ObservableProperty] private string _endpoint = "127.0.0.1:502";

    // ── Modbus ──
    [ObservableProperty] private int _unitId = 1;
    [ObservableProperty] private string _dataFormat = "ABCD";
    [ObservableProperty] private int _baudRate = 9600;
    [ObservableProperty] private string _parity = "None";
    [ObservableProperty] private int _dataBits = 8;
    [ObservableProperty] private string _stopBits = "One";

    [ObservableProperty] private int _rack;
    [ObservableProperty] private int _slot = 1;
    [ObservableProperty] private string _cpuType = "S-1200";
    [ObservableProperty] private string _pingAddress = "DB1.DBW0";

    // ── 连接参数 ──
    [ObservableProperty] private int _connectTimeoutMs = 3000;
    [ObservableProperty] private int _requestTimeoutMs = 5000;
    [ObservableProperty] private int _retryCount = 3;
    [ObservableProperty] private int _retryIntervalMs = 1000;

    // ── OPC UA 安全（ADR-073 层4；对齐 web OpcUaDeviceForm.vue）。空串 = 未声明（键不落库，后端安全优先）。 ──
    [ObservableProperty] private string _securityPolicy = "";
    [ObservableProperty] private string _securityMode = "";
    [ObservableProperty] private string _userName = "";

    /// <summary>新输入的密码：编辑态留空 = 不修改（沿用 <see cref="ExistingPassword"/>）；密文不回填。</summary>
    [ObservableProperty] private string _password = "";

    /// <summary>已落库密码（明文，仓储读时解密）。仅在编辑态有值，用于「留空=不改」语义。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPassword))]
    private string? _existingPassword;

    /// <summary>协议是否锁定（OPC UA 分区的新增/编辑表单只允许 OPC UA，对齐 web 独立表单不出现协议下拉）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProtocolItems))]
    [NotifyPropertyChangedFor(nameof(IsProtocolEditable))]
    private bool _lockProtocol;

    /// <summary>协议下拉是否可编辑（锁定时禁用，仅显示 OPC UA）。</summary>
    public bool IsProtocolEditable => !LockProtocol;

    /// <summary>是否为新增（调用方置位；用于窗口标题与默认值选择）。</summary>
    public bool IsNew { get; set; }

    public bool IsModbus => ProtocolName == "Modbus";
    public bool IsS7 => ProtocolName == "S7";
    public bool IsOpcUa => ProtocolName == "OPC UA";
    public bool IsRtu => IsModbus && Dialect == "RTU";

    /// <summary>传输方式是否可编辑：仅 Modbus 可切换 TCP/RTU；S7 固定 TCP、OPC UA 固定 opc.tcp（对齐 web DeviceForm.vue）。</summary>
    public bool IsDialectEditable => IsModbus;

    /// <summary>传输方式下拉可选值：S7 只有 TCP、OPC UA 只有 opc.tcp（禁用态锁定显示）。</summary>
    public IReadOnlyList<string> DialectItems => ProtocolName switch
    {
        "S7" => ["TCP"],
        "OPC UA" => ["opc.tcp"],
        _ => ["TCP", "RTU"]
    };

    /// <summary>协议下拉可选值：OPC UA 分区锁定为 OPC UA；通用分区仅 Modbus/S7（对齐 web 两个设备表单）。</summary>
    public IReadOnlyList<string> ProtocolItems => LockProtocol ? ["OPC UA"] : ["Modbus", "S7"];

    /// <summary>是否已设密码（编辑态提示「留空=不修改」）。</summary>
    public bool HasPassword => !string.IsNullOrEmpty(ExistingPassword);

    /// <summary>安全策略下拉：首项空串 = 自动（未声明，后端按安全优先）；None 须显式选择。</summary>
    public IReadOnlyList<string> SecurityPolicyItems => ["", "None", "Basic128Rsa15", "Basic256", "Basic256Sha256"];

    /// <summary>安全模式下拉：首项空串 = 自动。</summary>
    public IReadOnlyList<string> SecurityModeItems => ["", "None", "Sign", "SignAndEncrypt"];

    /// <summary>连接地址字段标签（按协议给占位提示）：OPC UA 填 opc.tcp:// 端点、S7 端口 102、Modbus TCP IP:502 / RTU COM 口。</summary>
    public string EndpointLabel => IsOpcUa
        ? "连接地址（opc.tcp://IP:端口）"
        : IsS7
            ? "连接地址（IP:102）"
            : "连接地址（串口填 COM3 等）";

    /// <summary>测试按钮可用性：已注入测试服务且当前未在测试。</summary>
    public bool IsTestEnabled => ConnectionTester is not null && !IsTestingConnection;

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

    public bool Validate()
    {
        SetError(nameof(Name), string.IsNullOrWhiteSpace(Name) ? "设备名称不能为空" : null);
        SetError(nameof(Endpoint),
            string.IsNullOrWhiteSpace(Endpoint)
                ? "连接地址不能为空（TCP 填 IP:端口，RTU 填 COM 口）"
                : IsOpcUa && !Endpoint.StartsWith("opc.tcp://", StringComparison.OrdinalIgnoreCase)
                    ? "OPC UA 端点须以 opc.tcp:// 开头，如 opc.tcp://127.0.0.1:4840"
                    : null);
        SetError(nameof(UnitId), IsModbus && UnitId is < 1 or > 247 ? "从站地址须在 1-247" : null);
        SetError(nameof(Rack), IsS7 && Rack is < 0 or > 7 ? "Rack 须在 0-7" : null);
        SetError(nameof(Slot), IsS7 && Slot is < 0 or > 31 ? "Slot 须在 0-31" : null);
        SetError(nameof(ConnectTimeoutMs), ConnectTimeoutMs <= 0 ? "连接超时须大于 0" : null);
        SetError(nameof(RequestTimeoutMs), RequestTimeoutMs <= 0 ? "请求超时须大于 0" : null);
        SetError(nameof(RetryCount), RetryCount <= 0 ? "重试次数须大于 0" : null);
        SetError(nameof(RetryIntervalMs), RetryIntervalMs <= 0 ? "重试间隔须大于 0" : null);
        SetError(nameof(BaudRate), IsRtu && !ValidBaudRates.Contains(BaudRate) ? "波特率须为 9600/19200/38400/57600/115200" : null);
        SetError(nameof(DataBits), IsRtu && DataBits is not (7 or 8) ? "数据位须为 7 或 8" : null);
        // OPC UA 凭据：用户名与密码须成对（对齐后端 OpcUaSecurityParameters 校验口径）
        var hasUser = !string.IsNullOrWhiteSpace(UserName);
        var hasPassword = !string.IsNullOrEmpty(Password) || !string.IsNullOrEmpty(ExistingPassword);
        SetError(nameof(UserName), IsOpcUa && !hasUser && hasPassword ? "已设密码，须同时填写用户名（或清除密码）" : null);
        SetError(nameof(Password), IsOpcUa && hasUser && !hasPassword ? "已填用户名，须同时填写密码" : null);
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
    partial void OnDialectChanged(string value) => Validate();
    partial void OnUnitIdChanged(int value) => Validate();
    partial void OnRackChanged(int value) => Validate();
    partial void OnSlotChanged(int value) => Validate();
    partial void OnConnectTimeoutMsChanged(int value) => Validate();
    partial void OnRequestTimeoutMsChanged(int value) => Validate();
    partial void OnRetryCountChanged(int value) => Validate();
    partial void OnRetryIntervalMsChanged(int value) => Validate();
    partial void OnBaudRateChanged(int value) => Validate();
    partial void OnDataBitsChanged(int value) => Validate();
    partial void OnUserNameChanged(string value) => Validate();
    partial void OnPasswordChanged(string value) => Validate();
    partial void OnExistingPasswordChanged(string? value) => Validate();

    /// <summary>
    /// 协议切换联动（docs/13，对齐 web DeviceForm.vue onProtocolChange/onDialectChange）：
    /// 传输方式按协议锁定（S7→TCP、OPC UA→opc.tcp 仅显示）、端点命中其他协议默认值或为空时
    /// 换成本协议默认端点（保留用户自定义端点）；OPC UA 无方言，ToDevice 时不落库。
    /// </summary>
    partial void OnProtocolNameChanged(string value)
    {
        if (IsModbus)
        {
            if (Dialect is not ("TCP" or "RTU"))
                Dialect = "TCP";
            if (string.IsNullOrWhiteSpace(Endpoint) || Endpoint == "127.0.0.1:102" || Endpoint == "opc.tcp://127.0.0.1:4840")
                Endpoint = "127.0.0.1:502";
        }
        else if (IsS7)
        {
            Dialect = "TCP";
            if (string.IsNullOrWhiteSpace(Endpoint) || Endpoint == "127.0.0.1:502" || Endpoint == "opc.tcp://127.0.0.1:4840")
                Endpoint = "127.0.0.1:102";
        }
        else // OPC UA
        {
            Dialect = "opc.tcp"; // 仅 UI 显示；ToDevice 时置 null（后端 ProtocolIdentifier.OpcUa 无方言）
            if (string.IsNullOrWhiteSpace(Endpoint) || Endpoint == "127.0.0.1:502" || Endpoint == "127.0.0.1:102")
                Endpoint = "opc.tcp://127.0.0.1:4840";
        }
        Validate();
    }

    [RelayCommand(CanExecute = nameof(IsTestEnabled))]
    private async Task TestConnectionAsync()
    {
        if (ConnectionTester is null)
            return;

        IsTestingConnection = true;
        TestResultText = "测试中…";
        try
        {
            var result = await ConnectionTester.TestAsync(ToDevice());
            TestResultText = result.Success
                ? $"连接成功 ({result.LatencyMs}ms)"
                : $"连接失败: {result.Error}";
        }
        catch (Exception ex)
        {
            TestResultText = $"测试异常: {ex.Message}";
        }
        finally
        {
            IsTestingConnection = false;
        }
    }

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
        else if (IsOpcUa)
        {
            // ADR-073 层4：键为空即不落库（后端按安全优先默认）；密码留空沿用既有（PreserveExistingCredential 语义）
            if (!string.IsNullOrWhiteSpace(SecurityPolicy))
                parameters["SecurityPolicy"] = SecurityPolicy;
            if (!string.IsNullOrWhiteSpace(SecurityMode))
                parameters["SecurityMode"] = SecurityMode;
            if (!string.IsNullOrWhiteSpace(UserName))
                parameters["UserName"] = UserName.Trim();
            var password = string.IsNullOrEmpty(Password) ? ExistingPassword : Password;
            if (!string.IsNullOrEmpty(password))
                parameters["Password"] = password;
        }

        return new Device
        {
            Id = Id,
            Name = Name,
            Description = Description,
            // OPC UA 方言为 null：后端 ProtocolIdentifier.OpcUa 无 Dialect（web 同语义）
            Protocol = new ProtocolIdentifier { Name = ProtocolName, Dialect = IsOpcUa ? null : Dialect },
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
        var protocolName = Normalize(device.Protocol.Name);
        var dialect = Normalize(string.IsNullOrEmpty(device.Protocol.Dialect)
            ? protocolName == "OPC UA" ? "opc.tcp" : "TCP"
            : device.Protocol.Dialect);
        return new DeviceEditor
        {
            Id = device.Id,
            Name = device.Name,
            Description = device.Description,
            // "System.Windows.Controls.ComboBoxItem: Modbus"，回填时归一化为纯值，
            // 保证旧脏数据重编辑后保存即可恢复采集。
            ProtocolName = protocolName,
            Dialect = dialect,
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
            PingAddress = p.GetValueOrDefault("PingAddress")?.ToString() ?? "DB1.DBW0",
            // OPC UA 安全（ADR-073 层4）：密码密文不回填，仅记录「已设置」以支持留空=不修改
            SecurityPolicy = p.GetValueOrDefault("SecurityPolicy")?.ToString() ?? "",
            SecurityMode = p.GetValueOrDefault("SecurityMode")?.ToString() ?? "",
            UserName = p.GetValueOrDefault("UserName")?.ToString() ?? "",
            ExistingPassword = p.GetValueOrDefault("Password")?.ToString()
        };
    }

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
