using System.Collections;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using NitroGateway.Domain.Devices;

namespace NitroGateway.Desktop.ViewModels;

/// <summary>
/// 点位表单编辑模型（ADR-029 P3），字段与 Web PointList.vue 对齐。
/// 地址提示按设备协议区分（Modbus 40001 / S7 DB1.DBD0 / OPC UA ns=2;i=1001，docs/13）。
/// </summary>
public sealed partial class PointEditor : ObservableObject, INotifyDataErrorInfo
{
    /// <summary>字段级错误表（属性名 -> 错误文案，由 Validate() 全量重算）。</summary>
    private readonly Dictionary<string, string> _errors = [];

    /// <summary>点位 ID：新建由调用方生成，编辑保留原 ID</summary>
    public Guid Id { get; set; }

    /// <summary>所属设备协议（Modbus / S7 / OPC UA），由设备列表透传，仅用于地址提示与校验文案。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AddressHint))]
    private string _protocolName = "Modbus";

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _address = "";
    [ObservableProperty] private string? _description;
    [ObservableProperty] private DataType _dataType = DataType.Float;
    [ObservableProperty] private bool _enabled = true;
    [ObservableProperty] private PointAccess _access = PointAccess.ReadOnly;

    /// <summary>采集间隔（毫秒）；0 表示继承设备默认间隔</summary>
    [ObservableProperty] private int _scanIntervalMs;

    /// <summary>值变化死区（仅模拟量有效），0 表示不启用</summary>
    [ObservableProperty] private double _deadband;

    [ObservableProperty] private double _scaleFactor = 1.0;
    [ObservableProperty] private double _scaleOffset;

    /// <summary>是否存在校验错误（ADR-037 S4）。</summary>
    public bool HasErrors => _errors.Count > 0;

    /// <summary>
    /// 地址输入提示（按协议，docs/13）：
    /// Modbus "如 40001"、S7 "如 DB1.DBD0"、OPC UA "如 ns=2;i=1001"。
    /// </summary>
    public string AddressHint => ProtocolName switch
    {
        "S7" => "如 DB1.DBD0",
        "OPC UA" => "如 ns=2;i=1001",
        _ => "如 40001"
    };

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
    /// 规则：Name/Address 非空；ScanIntervalMs/Deadband 非负；缩放系数与偏移为有限数值。
    /// 返回是否通过（窗口保存前调用）。
    /// </summary>
    public bool Validate()
    {
        SetError(nameof(Name), string.IsNullOrWhiteSpace(Name) ? "点位名称不能为空" : null);
        SetError(nameof(Address), string.IsNullOrWhiteSpace(Address) ? $"点位地址不能为空（{AddressHint}）" : null);
        SetError(nameof(ScanIntervalMs), ScanIntervalMs < 0 ? "采集间隔不能为负（0=继承设备默认）" : null);
        SetError(nameof(Deadband), Deadband < 0 || double.IsNaN(Deadband) ? "死区不能为负" : null);
        SetError(nameof(ScaleFactor), !IsFinite(ScaleFactor) ? "缩放系数必须为有限数值" : null);
        SetError(nameof(ScaleOffset), !IsFinite(ScaleOffset) ? "缩放偏移必须为有限数值" : null);
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
    partial void OnNameChanged(string value) => Validate();
    partial void OnAddressChanged(string value) => Validate();
    partial void OnScanIntervalMsChanged(int value) => Validate();
    partial void OnDeadbandChanged(double value) => Validate();
    partial void OnScaleFactorChanged(double value) => Validate();
    partial void OnScaleOffsetChanged(double value) => Validate();

    /// <summary>double 有限性判断（拒绝 NaN/正负无穷）。</summary>
    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    /// <summary>由表单构建设点实体</summary>
    public DevicePoint ToPoint() => new()
    {
        Id = Id,
        Name = Name,
        Address = Address,
        Description = Description,
        DataType = DataType,
        Enabled = Enabled,
        Access = Access,
        ScanIntervalMs = ScanIntervalMs,
        Deadband = Deadband,
        ScaleFactor = ScaleFactor,
        ScaleOffset = ScaleOffset
    };

    /// <summary>由已有点位回填表单（编辑场景）</summary>
    public static PointEditor FromPoint(DevicePoint point) => new()
    {
        Id = point.Id,
        Name = point.Name,
        Address = point.Address,
        Description = point.Description,
        DataType = point.DataType,
        Enabled = point.Enabled,
        Access = point.Access,
        ScanIntervalMs = point.ScanIntervalMs,
        Deadband = point.Deadband,
        ScaleFactor = point.ScaleFactor,
        ScaleOffset = point.ScaleOffset
    };
}
