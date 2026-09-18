using System.Windows;
using NitroGateway.Desktop.ViewModels;

namespace NitroGateway.Desktop.Views;

public partial class DeviceEditorWindow : Window
{
    /// <param name="editor">设备表单模型（绑定 DataContext）</param>
    public DeviceEditorWindow(DeviceEditor editor)
    {
        InitializeComponent();
        DataContext = editor;
        Title = editor.Id == Guid.Empty ? "新增设备" : "编辑设备";
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (DataContext is DeviceEditor editor && !editor.Validate())
            return;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
