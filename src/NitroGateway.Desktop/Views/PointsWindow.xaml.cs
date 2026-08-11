using System.Windows;
using NitroGateway.Desktop.ViewModels;

namespace NitroGateway.Desktop.Views;

/// <summary>点位管理窗口（ADR-029 P2）：DataContext 为 PointsViewModel。</summary>
public partial class PointsWindow : Window
{
    public PointsWindow(PointsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Title = $"点位管理 - {viewModel.DeviceName}";
    }
}
