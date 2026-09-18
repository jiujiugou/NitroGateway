using System.Windows;
using NitroGateway.Desktop.ViewModels;

namespace NitroGateway.Desktop.Views;

public partial class PointEditorWindow : Window
{
    public PointEditorWindow(PointEditor editor)
    {
        InitializeComponent();
        DataContext = editor;
        Title = editor.Id == Guid.Empty ? "添加点位" : "编辑点位";
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (DataContext is PointEditor editor && !editor.Validate())
            return;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
