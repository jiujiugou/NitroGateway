using System.Windows;
using NitroGateway.Desktop.ViewModels;

namespace NitroGateway.Desktop.Views;

/// <summary>告警规则表单窗口（ADR-043）：DataContext 为 AlarmRuleEditor。</summary>
public partial class AlarmRuleEditorWindow : Window
{
    public AlarmRuleEditorWindow(AlarmRuleEditor editor)
    {
        InitializeComponent();
        DataContext = editor;
        Title = editor.Id == Guid.Empty ? "添加告警规则" : "编辑告警规则";
    }

    /// <summary>保存（ADR-037 S4 模式）：校验未通过时不关窗，错误经 ErrorTemplate 行内提示。</summary>
    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (DataContext is AlarmRuleEditor editor && !editor.Validate())
            return;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
