using System.Windows;
using NitroGateway.Desktop.ViewModels;

namespace NitroGateway.Desktop.Views;

/// <summary>设备表单窗口（ADR-029 P3）：DataContext 为 DeviceEditor，保存/取消设置 DialogResult。</summary>
public partial class DeviceEditorWindow : Window
{
    public DeviceEditorWindow(DeviceEditor editor)
    {
        InitializeComponent();
        DataContext = editor;
        Title = editor.Id == Guid.Empty ? "新增设备" : "编辑设备";
    }

    private void OnSave(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
