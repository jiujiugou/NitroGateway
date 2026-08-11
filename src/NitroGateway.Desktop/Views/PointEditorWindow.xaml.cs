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

    private void OnSave(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
