using System.Windows;
using NitroGateway.Desktop.ViewModels;
using NitroGateway.Desktop.Views;

namespace NitroGateway.Desktop.Services;

/// <summary>WPF 对话框实现（模态 Window，Owner 取主窗口）。</summary>
public sealed class DeviceDialogService : IDeviceDialogService
{
    private readonly IDeviceConnectionTester _connectionTester;
    private readonly IPointsViewModelFactory _pointsFactory;

    public DeviceDialogService(
        IDeviceConnectionTester connectionTester,
        IPointsViewModelFactory pointsFactory)
    {
        _connectionTester = connectionTester;
        _pointsFactory = pointsFactory;
    }

    /// <inheritdoc />
    public bool EditDevice(DeviceEditor editor)
    {
        // ADR-044：把连接测试服务注入表单模型，「测试连接」按钮命令在本机做 Connect+Ping
        editor.ConnectionTester = _connectionTester;
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
        // ADR-029 P2：ViewModel 构造与 scope 依赖解析收敛到工厂，对话框只依赖工厂接口
        var viewModel = _pointsFactory.Create(deviceId, deviceName);
        var window = new PointsWindow(viewModel) { Owner = Application.Current?.MainWindow };
        window.ShowDialog();
        viewModel.Dispose();
    }
}
