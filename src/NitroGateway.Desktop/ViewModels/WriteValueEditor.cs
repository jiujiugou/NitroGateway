using CommunityToolkit.Mvvm.ComponentModel;

namespace NitroGateway.Desktop.ViewModels;

/// <summary>
/// 写值编辑器模型（UI 骨架，docs/14 §5.2）：展示目标设备/点位/地址/类型/当前值/范围（只读），
/// 承载用户输入的新值（<see cref="InputValue"/> 供数值/字符串，<see cref="BoolValue"/> 供 Bool）。
/// 字段与 RealtimeView 行内编辑器（行内就地输入，借鉴 ThingsGateway）绑定对齐；
/// 真正写值逻辑由调用方在确认后执行（WriteService 统一链路）。
/// </summary>
public sealed partial class WriteValueEditor : ObservableObject
{
    /// <summary>目标设备 ID（写执行时作为 WriteRequest.DeviceId 使用）</summary>
    public required Guid DeviceId { get; init; }

    /// <summary>目标点位 ID（写执行时作为 WriteRequest.PointId 使用）</summary>
    public required Guid PointId { get; init; }

    /// <summary>目标设备名称（只读展示）</summary>
    public required string DeviceName { get; init; }

    /// <summary>目标点位名称（只读展示）</summary>
    public required string PointName { get; init; }

    /// <summary>点位地址（只读展示）</summary>
    public required string Address { get; init; }

    /// <summary>点位数据类型（只读展示，如 Float / Bool / String）</summary>
    public required string DataType { get; init; }

    /// <summary>当前值文本（只读展示）</summary>
    public required string CurrentValueText { get; init; }

    /// <summary>可写范围提示（只读展示，如“0 ~ 100”或“不限”）</summary>
    public required string RangeText { get; init; }

    /// <summary>用户输入的新值（字符串，数值/字符串点位由调用方按 DataType 解析/转换后下发）</summary>
    [ObservableProperty]
    private string _inputValue = "";

    /// <summary>Bool 点位的输入值（UI 用 CheckBox 绑定；非 Bool 点位忽略）</summary>
    [ObservableProperty]
    private bool _boolValue;

    /// <summary>是否为 Bool 点位（行内编辑器据此切换 CheckBox / TextBox 输入控件）</summary>
    public bool IsBool => string.Equals(DataType, "Bool", StringComparison.Ordinal);
}
