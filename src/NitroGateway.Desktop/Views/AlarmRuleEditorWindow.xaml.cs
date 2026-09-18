using System.Windows;
using NitroGateway.Desktop.ViewModels;

namespace NitroGateway.Desktop.Views;

public partial class AlarmRuleEditorWindow : Window
{
    public AlarmRuleEditorWindow(AlarmRuleEditor editor)
    {
        InitializeComponent();
        DataContext = editor;
        Title = editor.Id == Guid.Empty ? "添加告警规则" : "编辑告警规则";
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (DataContext is AlarmRuleEditor editor && !editor.Validate())
            return;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
