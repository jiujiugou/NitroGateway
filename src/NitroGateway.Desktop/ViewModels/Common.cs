using CommunityToolkit.Mvvm.ComponentModel;

namespace NitroGateway.Desktop.ViewModels;

/// <summary>设备下拉选项</summary>
public sealed record DeviceOption(Guid Id, string Name);

/// <summary>点位下拉选项</summary>
public sealed record PointOption(Guid Id, string Name, string Address);

/// <summary>左侧导航项（标题 + 页面 ViewModel）</summary>
public sealed record NavItem(string Title, ObservableObject ViewModel);
