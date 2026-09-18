using System.Windows;
using NitroGateway.Desktop.ViewModels;

namespace NitroGateway.Desktop.Views;

public partial class PointsWindow : Window
{
    public PointsWindow(PointsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Title = $"点位管理 - {viewModel.DeviceName}";
    }
}
