using CommunityToolkit.Mvvm.ComponentModel;
using NitroGateway.Domain.Devices;

namespace NitroGateway.Desktop.ViewModels;

/// <summary>点位表单编辑模型（ADR-029 P3），字段与 Web PointList.vue 对齐。</summary>
public sealed partial class PointEditor : ObservableObject
{
    /// <summary>点位 ID：新建由调用方生成，编辑保留原 ID</summary>
    public Guid Id { get; set; }

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
