using System.Windows;
using NitroGateway.Desktop.ViewModels;

namespace NitroGateway.Desktop.Views;

/// <summary>批量生成点位窗口（docs/13）：DataContext 为 PointBatchEditor。</summary>
public partial class PointBatchWindow : Window
{
    public PointBatchWindow(PointBatchEditor editor)
    {
        InitializeComponent();
        DataContext = editor;
    }

    /// <summary>生成（ADR-037 S4）：校验未通过时不关窗，错误经 ErrorTemplate 行内提示。</summary>
    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (DataContext is PointBatchEditor editor && !editor.Validate())
            return;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
