using System.Windows;
using NitroGateway.Desktop.ViewModels;

namespace NitroGateway.Desktop.Views;

/// <summary>点位表单窗口（ADR-029 P3）：DataContext 为 PointEditor。</summary>
public partial class PointEditorWindow : Window
{
    public PointEditorWindow(PointEditor editor)
    {
        InitializeComponent();
        DataContext = editor;
        Title = editor.Id == Guid.Empty ? "添加点位" : "编辑点位";
    }

    /// <summary>保存（ADR-037 S4）：校验未通过时不关窗，错误经 ErrorTemplate 行内提示。</summary>
    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (DataContext is PointEditor editor && !editor.Validate())
            return;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
