using System.Windows;
using NitroGateway.Desktop.ViewModels;

namespace NitroGateway.Desktop.Views;

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
