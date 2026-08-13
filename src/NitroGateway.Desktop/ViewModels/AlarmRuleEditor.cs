using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using NitroGateway.Alarm.Domain;
using NitroGateway.Domain.Devices;

namespace NitroGateway.Desktop.ViewModels;

/// <summary>
/// 告警规则表单编辑模型（ADR-043）。可变对象供 WPF 双向绑定；
/// 设备切换时级联刷新点位下拉（<see cref="Points"/>），与 Web AlarmRulesView.vue 交互一致。
/// 字段集与 AlarmRule 领域模型 + AlarmRulesController 对齐。
/// </summary>
public sealed partial class AlarmRuleEditor : ObservableObject, INotifyDataErrorInfo
{
    /// <summary>支持的比较运算符（与 ThresholdEvaluator 解释一致）。</summary>
    public static readonly IReadOnlyList<string> Operators =
        [">", ">=", "<", "<=", "==", "!=", "Between"];

    /// <summary>字段级错误表（属性名 -> 错误文案，由 Validate() 全量重算）。</summary>
    private readonly Dictionary<string, string> _errors = [];

    private readonly IReadOnlyList<Device> _devices;

    /// <summary>设备下拉选项（只读列表，设备切换不重建）。</summary>
    public IReadOnlyList<DeviceOption> Devices { get; }

    /// <summary>当前设备点位下拉选项（随 <see cref="DeviceId"/> 切换级联刷新）。</summary>
    public ObservableCollection<PointOption> Points { get; } = [];

    /// <summary>规则 ID：新建由调用方生成，编辑保留原 ID。</summary>
    public Guid Id { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDevice))]
    private Guid _deviceId;

    [ObservableProperty] private Guid _pointId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBetween))]
    private string _operator = ">";

    /// <summary>阈值；Between 模式表示下限。</summary>
    [ObservableProperty] private double _threshold = 80;

    /// <summary>Between 模式上限。</summary>
    [ObservableProperty] private double? _thresholdUpper;

    /// <summary>持续时长（秒），0=立即触发。</summary>
    [ObservableProperty] private int _durationSeconds;

    [ObservableProperty] private AlarmSeverity _severity = AlarmSeverity.Warning;

    /// <summary>消息模板，{value}/{threshold} 占位符由告警评估层替换。</summary>
    [ObservableProperty] private string? _messageTemplate;

    [ObservableProperty] private bool _enabled = true;

    /// <summary>是否已选设备（点位级联/校验依据）。</summary>
    public bool HasDevice => DeviceId != Guid.Empty;

    /// <summary>运算符是否为 Between（控制上限输入显隐）。</summary>
    public bool IsBetween => Operator == "Between";

    /// <summary>
    /// 创建表单。
    /// </summary>
    /// <param name="devices">设备全量（含点位），来自 IDeviceSnapshotCache，只读不改。</param>
    public AlarmRuleEditor(IReadOnlyList<Device> devices)
    {
        _devices = devices;
        Devices = devices.Select(d => new DeviceOption(d.Id, d.Name)).ToList();
        RebuildPoints();
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

    // 设备切换：级联刷新点位下拉；其余字段变更即重算校验（字段少、全量校验开销可忽略）
    partial void OnDeviceIdChanged(Guid value)
    {
        RebuildPoints();
        Validate();
    }

    partial void OnPointIdChanged(Guid value) => Validate();
    partial void OnOperatorChanged(string value) => Validate();
    partial void OnThresholdChanged(double value) => Validate();
    partial void OnThresholdUpperChanged(double? value) => Validate();
    partial void OnDurationSecondsChanged(int value) => Validate();
    partial void OnMessageTemplateChanged(string? value) => Validate();

    /// <summary>按当前设备重建点位下拉；设备未选/点位不存在时清空选择。</summary>
    private void RebuildPoints()
    {
        Points.Clear();
        var device = _devices.FirstOrDefault(d => d.Id == DeviceId);
        if (device is not null)
        {
            foreach (var point in device.Points)
                Points.Add(new PointOption(point.Id, point.Name, point.Address));
        }
        if (!Points.Any(p => p.Id == PointId))
            PointId = Guid.Empty;
    }

    /// <summary>
    /// 全表单校验：设备/点位必选、运算符合法、阈值有限、Between 上限须 >= 下限、时长非负。
    /// 返回是否通过（窗口保存前调用）。
    /// </summary>
    public bool Validate()
    {
        SetError(nameof(DeviceId), DeviceId == Guid.Empty ? "请选择设备" : null);
        SetError(nameof(PointId), PointId == Guid.Empty ? "请选择点位" : null);
        SetError(nameof(Operator), !Operators.Contains(Operator) ? "运算符不合法" : null);
        SetError(nameof(Threshold), !IsFinite(Threshold) ? "阈值必须为有限数值" : null);

        if (IsBetween)
        {
            SetError(nameof(ThresholdUpper),
                !ThresholdUpper.HasValue ? "Between 模式须填写上限" :
                !IsFinite(ThresholdUpper.Value) ? "上限必须为有限数值" :
                ThresholdUpper.Value < Threshold ? "上限不能小于下限" : null);
        }
        else
        {
            SetError(nameof(ThresholdUpper), null);
        }

        SetError(nameof(DurationSeconds), DurationSeconds < 0 ? "持续时长不能为负（0=立即触发）" : null);
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

    /// <summary>double 有限性判断（拒绝 NaN/正负无穷）。</summary>
    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    /// <summary>由表单构建领域规则；非 Between 模式清空上限（与 Web 保存逻辑一致）。</summary>
    public AlarmRule ToRule() => new()
    {
        Id = Id,
        DeviceId = DeviceId,
        PointId = PointId,
        Operator = Operator,
        Threshold = Threshold,
        ThresholdUpper = IsBetween ? ThresholdUpper : null,
        DurationSeconds = DurationSeconds,
        Severity = Severity,
        MessageTemplate = string.IsNullOrWhiteSpace(MessageTemplate) ? null : MessageTemplate,
        Enabled = Enabled
    };

    /// <summary>由已有规则回填表单（编辑场景）。</summary>
    public static AlarmRuleEditor FromRule(AlarmRule rule, IReadOnlyList<Device> devices) => new(devices)
    {
        Id = rule.Id,
        DeviceId = rule.DeviceId,
        PointId = rule.PointId,
        Operator = rule.Operator,
        Threshold = rule.Threshold,
        ThresholdUpper = rule.ThresholdUpper,
        DurationSeconds = rule.DurationSeconds,
        Severity = rule.Severity,
        MessageTemplate = rule.MessageTemplate,
        Enabled = rule.Enabled
    };
}
