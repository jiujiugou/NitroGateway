using System.Windows;

namespace NitroGateway.Desktop.Views;

/// <summary>
/// ADR-037 S8：启动反馈窗口。宿主启动期间显示进度；
/// 启动失败时在窗口内展示错误与关闭按钮（不再只弹 MessageBox）。
/// </summary>
public partial class StartupWindow : Window
{
    public StartupWindow() => InitializeComponent();

    /// <summary>把窗口切换为失败态：停进度、显示错误文案与关闭按钮。</summary>
    public void ShowError(string message)
    {
        StartupProgress.IsIndeterminate = false;
        StartupProgress.Visibility = Visibility.Collapsed;
        StatusText.Text = $"启动失败：{message}";
        StatusText.Foreground = (System.Windows.Media.Brush)FindResource("ErrorBrush");
        CloseButton.Visibility = Visibility.Visible;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
