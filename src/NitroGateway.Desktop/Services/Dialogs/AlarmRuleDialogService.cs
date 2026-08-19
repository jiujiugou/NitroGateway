using System.Windows;
using NitroGateway.Desktop.ViewModels;
using NitroGateway.Desktop.Views;

namespace NitroGateway.Desktop.Services.Dialogs;

/// <summary>WPF 告警规则对话框实现（模态 Window，Owner 取主窗口，仿 DeviceDialogService）。</summary>
public sealed class AlarmRuleDialogService : IAlarmRuleDialogService
{
    /// <inheritdoc />
    public bool EditRule(AlarmRuleEditor editor)
    {
        var window = new AlarmRuleEditorWindow(editor) { Owner = Application.Current?.MainWindow };
        return window.ShowDialog() == true;
    }

    /// <inheritdoc />
    public bool Confirm(string title, string message) =>
        MessageBox.Show(Application.Current?.MainWindow, message, title,
            MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
}
