using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NitroGateway.Desktop.ViewModels;
using NitroGateway.Desktop.Views;

namespace NitroGateway.Desktop.Services;

/// <summary>WPF 对话框实现（模态 Window，Owner 取主窗口）。</summary>
public sealed class DeviceDialogService : IDeviceDialogService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfigSyncOutboxStore _outbox;
    private readonly ILogger<PointsViewModel> _logger;

    public DeviceDialogService(
        IServiceScopeFactory scopeFactory,
        IConfigSyncOutboxStore outbox,
        ILogger<PointsViewModel> logger)
    {
        _scopeFactory = scopeFactory;
        _outbox = outbox;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool EditDevice(DeviceEditor editor)
    {
        var window = new DeviceEditorWindow(editor) { Owner = Application.Current?.MainWindow };
        return window.ShowDialog() == true;
    }

    /// <inheritdoc />
    public bool EditPoint(PointEditor editor)
    {
        var window = new PointEditorWindow(editor) { Owner = Application.Current?.MainWindow };
        return window.ShowDialog() == true;
    }

    /// <inheritdoc />
    public bool Confirm(string title, string message) =>
        MessageBox.Show(Application.Current?.MainWindow, message, title,
            MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    /// <inheritdoc />
    public void ShowPoints(Guid deviceId, string deviceName)
    {
        var viewModel = new PointsViewModel(deviceId, deviceName, _scopeFactory, this, _outbox, _logger);
        var window = new PointsWindow(viewModel) { Owner = Application.Current?.MainWindow };
        window.ShowDialog();
        viewModel.Dispose();
    }
}
