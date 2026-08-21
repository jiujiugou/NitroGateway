using System.Collections;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using NitroGateway.Domain.Devices;

namespace NitroGateway.Desktop.ViewModels;

/// <summary>
/// 点位批量生成表单模型（docs/13，对齐 Web PointList.vue 批量生成对话框）。
/// 起始地址与递增规则按设备协议区分：Modbus 数字寄存器、S7 DB 区地址（按类型字节宽）、
/// OPC UA 数值标识符（ns={n};i={id} 逐点 +1）。生成逻辑复用 <c>PointBatchService.Generate</c>。
/// </summary>
public sealed partial class PointBatchEditor : ObservableObject, INotifyDataErrorInfo
{
    /// <summary>字段级错误表（属性名 -> 错误文案，由 Validate() 全量重算）。</summary>
    private readonly Dictionary<string, string> _errors = [];

    /// <summary>所属设备协议（Modbus / S7 / OPC UA），决定起始地址默认值与递增提示。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AddressHint))]
    [NotifyPropertyChangedFor(nameof(GenHint))]
    private string _protocolName = "Modbus";

    /// <summary>名称模板：{###} 或裸 ### 替换为序号（零填充），如 AI_{###} → AI_001, AI_002。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewName))]
    private string _nameTemplate = "AI_{###}";

    /// <summary>起始地址（Modbus 如 40001 / S7 如 DB1.DBD0 / OPC UA 如 ns=2;i=1001）。</summary>
    [ObservableProperty] private string _startAddress = "40001";

    /// <summary>生成数量（1-5000，PointBatchService 上限）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GenHint))]
    private int _count = 100;

    [ObservableProperty] private DataType _dataType = DataType.Float;
    [ObservableProperty] private PointAccess _access = PointAccess.ReadOnly;

    public PointBatchEditor()
    {
        StartAddress = DefaultStartAddress(ProtocolName);
    }

    /// <summary>按协议返回默认起始地址（docs/13，对齐 web defaultStartAddress）。</summary>
    public static string DefaultStartAddress(string protocol) => protocol switch
    {
        "S7" => "DB1.DBD0",
        "OPC UA" => "ns=2;i=1001",
        _ => "40001"
    };

    /// <summary>地址输入提示（按协议）：Modbus "如 40001"、S7 "如 DB1.DBD0"、OPC UA "如 ns=2;i=1001"。</summary>
    public string AddressHint => ProtocolName switch
    {
        "S7" => "如 DB1.DBD0",
        "OPC UA" => "如 ns=2;i=1001",
        _ => "如 40001"
    };

    /// <summary>
    /// 名称模板首项预览（对齐 web previewName）：把第一个序号占位符替换为 001。
    /// 无占位符时原样返回（生成的点位同名）。
    /// </summary>
    public string PreviewName
    {
        get
        {
            var pad = NameTemplate.Count(c => c == '#');
            if (pad == 0)
                return NameTemplate;
            return NameTemplate.Replace(new string('#', pad), 1.ToString().PadLeft(pad, '0'));
        }
    }

    /// <summary>批量生成递增规则提示（对齐 web genHint）：OPC UA 仅数值标识符 i= 可自动 +1。</summary>
    public string GenHint
    {
        get
        {
            var (rule, extra) = ProtocolName switch
            {
                "S7" => ("类型字节宽度", "（DB 区，不支持 Bool）"),
                "OPC UA" => ("数值标识（i=）", "（如 ns=2;i=1001 → 1002，仅支持数值标识符）"),
                _ => ("Modbus 寄存器数", "")
            };
            return $"将生成 {Count} 个点位，地址按{rule}递增{extra}";
        }
    }

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
    /// 全表单校验：名称模板/起始地址非空、数量 1-5000。
    /// 起始地址格式与协议兼容性由 <c>PointBatchService.Generate</c> 抛出 ArgumentException，
    /// 在 GenerateBatchAsync 捕获并以状态栏提示（不阻塞落库语义）。
    /// </summary>
    public bool Validate()
    {
        SetError(nameof(NameTemplate), string.IsNullOrWhiteSpace(NameTemplate) ? "名称模板不能为空" : null);
        SetError(nameof(StartAddress), string.IsNullOrWhiteSpace(StartAddress) ? $"起始地址不能为空（{AddressHint}）" : null);
        SetError(nameof(Count), Count is < 1 or > 5000 ? "数量需在 1-5000" : null);
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

    // 字段变更即重算全表单（字段少、全量校验开销可忽略）
    partial void OnNameTemplateChanged(string value) => Validate();
    partial void OnStartAddressChanged(string value) => Validate();
    partial void OnCountChanged(int value) => Validate();

    /// <summary>
    /// 协议变更时联动起始地址（docs/13，对齐 DeviceEditor.OnProtocolNameChanged）：
    /// 仅当当前地址等于其他协议默认值或为空时替换为新协议默认值，保留用户自定义地址。
    /// </summary>
    partial void OnProtocolNameChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(StartAddress) || IsOtherProtocolDefault(StartAddress, value))
            StartAddress = DefaultStartAddress(value);
    }

    /// <summary>判断地址是否为其他协议的默认起始地址（避免切协议后仍显示旧协议默认值）。</summary>
    private static bool IsOtherProtocolDefault(string address, string protocol)
    {
        foreach (var candidate in new[] { "Modbus", "S7", "OPC UA" })
        {
            if (candidate != protocol && string.Equals(DefaultStartAddress(candidate), address, StringComparison.Ordinal))
                return true;
        }
        return false;
    }
}
