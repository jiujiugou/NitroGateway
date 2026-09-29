using System.Windows;
using NitroGateway.Desktop.ViewModels;

namespace NitroGateway.Desktop.Views;

public partial class OpcUaPointsWindow : Window
{
    public OpcUaPointsWindow(PointsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Title = $"OPC UA 点位 - {viewModel.DeviceName}";
    }
}
