using System.Windows;
using System.Windows.Controls;
using NitroGateway.Desktop.ViewModels;

namespace NitroGateway.Desktop.Views;

public partial class OpcUaPointEditorWindow : Window
{
    public OpcUaPointEditorWindow(OpcUaPointEditorViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Title = viewModel.Form.Id == Guid.Empty ? "从服务器选取 OPC UA 变量" : "编辑 OPC UA 点位";
    }

    /// <summary>TreeView.SelectedItem 为只读，用事件把选中节点推给 ViewModel（由 VM 回填表单）。</summary>
    private void OnTreeSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (DataContext is OpcUaPointEditorViewModel viewModel)
            viewModel.SelectedNode = e.NewValue as OpcUaTreeNode;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (DataContext is OpcUaPointEditorViewModel viewModel && !viewModel.Validate())
            return;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
