using System.Windows;
using NitroGateway.Desktop.ViewModels;

namespace NitroGateway.Desktop.Views;

/// <summary>
/// ADR-037 S8：启动反馈窗口。宿主启动期间显示进度；
/// 启动失败时在窗口内展示错误与关闭按钮（不再只弹 MessageBox）。
/// 状态由 <see cref="ViewModel"/> 绑定驱动，code-behind 只负责窗口生命周期。
/// </summary>
public partial class StartupWindow : Window
{
    /// <summary>启动状态 ViewModel（DataContext；App 直接驱动 <see cref="StartupViewModel.ShowError"/>）。</summary>
    public StartupViewModel ViewModel { get; } = new();

    public StartupWindow()
    {
        InitializeComponent();
        DataContext = ViewModel;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
