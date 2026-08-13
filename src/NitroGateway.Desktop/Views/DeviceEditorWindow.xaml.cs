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

    /// <summary>保存（ADR-037 S4）：校验未通过时不关窗，错误经 ErrorTemplate 行内提示。</summary>
    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (DataContext is DeviceEditor editor && !editor.Validate())
            return;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
