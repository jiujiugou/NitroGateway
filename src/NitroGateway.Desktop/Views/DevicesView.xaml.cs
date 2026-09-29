using System.Windows;
using System.Windows.Controls;
using NitroGateway.Desktop.ViewModels;

namespace NitroGateway.Desktop.Views;

public partial class DevicesView : UserControl
{
    public DevicesView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    /// <summary>
    /// 按协议分区切换列：通用（Modbus/S7）显示「从站」，OPC UA 显示「连接端点 + 安全档位」。
    /// DataGridColumn 不在可视/逻辑树中，无法用 RelativeSource 绑定 VM，故在 code-behind 按 Scope 设置。
    /// </summary>
    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        var opcUa = (e.NewValue as DevicesViewModel)?.Scope.OpcUaOnly ?? false;
        UnitIdColumn.Visibility = opcUa ? Visibility.Collapsed : Visibility.Visible;
        EndpointColumn.Visibility = opcUa ? Visibility.Visible : Visibility.Collapsed;
        SecurityColumn.Visibility = opcUa ? Visibility.Visible : Visibility.Collapsed;
    }
}
