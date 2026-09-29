using System.Windows;
using System.Windows.Controls;
using NitroGateway.Desktop.ViewModels;

namespace NitroGateway.Desktop.Views;

public partial class DeviceEditorWindow : Window
{
    /// <param name="editor">设备表单模型（绑定 DataContext）</param>
    public DeviceEditorWindow(DeviceEditor editor)
    {
        InitializeComponent();
        DataContext = editor;
        Title = editor switch
        {
            { LockProtocol: true, IsNew: true } => "新增 OPC UA 设备",
            { LockProtocol: true } => "编辑 OPC UA 设备",
            { IsNew: true } => "新增设备",
            _ => "编辑设备"
        };
    }

    /// <summary>
    /// PasswordBox 出于安全设计不暴露可绑定属性 → 由 code-behind 把输入推给表单模型，
    /// 使校验（用户名/密码成对）与保存（留空=沿用既有密码）走同一份状态。
    /// </summary>
    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is DeviceEditor editor && sender is PasswordBox box)
            editor.Password = box.Password;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (DataContext is DeviceEditor editor && !editor.Validate())
            return;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
