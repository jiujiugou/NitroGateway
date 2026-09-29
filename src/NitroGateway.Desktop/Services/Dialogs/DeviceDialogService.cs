using System.Windows;
using NitroGateway.Desktop.ViewModels;
using NitroGateway.Desktop.Views;
using NitroGateway.Desktop.Services.Connectivity;
using NitroGateway.Desktop.Services.Infrastructure;

namespace NitroGateway.Desktop.Services.Dialogs;

/// <summary>WPF 对话框实现（模态 Window，Owner 取主窗口）。</summary>
public sealed class DeviceDialogService : IDeviceDialogService
{
    private readonly IDeviceConnectionTester _connectionTester;
    private readonly IPointsViewModelFactory _pointsFactory;
    private readonly IOpcUaNodeBrowser _nodeBrowser;

    public DeviceDialogService(
        IDeviceConnectionTester connectionTester,
        IPointsViewModelFactory pointsFactory,
        IOpcUaNodeBrowser nodeBrowser)
    {
        _connectionTester = connectionTester;
        _pointsFactory = pointsFactory;
        _nodeBrowser = nodeBrowser;
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
    public bool EditOpcUaPoint(Guid deviceId, PointEditor editor)
    {
        // ADR-070 层次 1：OPC UA 点位走「左树右表单」，点选变量节点回填地址/类型/权限
        var viewModel = new OpcUaPointEditorViewModel(deviceId, editor, _nodeBrowser);
        try
        {
            var window = new OpcUaPointEditorWindow(viewModel) { Owner = Application.Current?.MainWindow };
            return window.ShowDialog() == true;
        }
        finally
        {
            viewModel.Dispose();
        }
    }

    /// <inheritdoc />
    public bool EditPointBatch(PointBatchEditor editor)
    {
        var window = new PointBatchWindow(editor) { Owner = Application.Current?.MainWindow };
        return window.ShowDialog() == true;
    }

    /// <inheritdoc />
    public bool EditWrite(WriteValueEditor editor)
    {
        var window = new WriteValueWindow { DataContext = editor, Owner = Application.Current?.MainWindow };
        return window.ShowDialog() == true;
    }

    /// <inheritdoc />
    public bool Confirm(string title, string message) =>
        MessageBox.Show(Application.Current?.MainWindow, message, title,
            MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    /// <inheritdoc />
    public void ShowPoints(Guid deviceId, string deviceName, string protocolName)
    {
        var viewModel = _pointsFactory.Create(deviceId, deviceName, protocolName);
        try
        {
            // 协议分区（对齐 web OPC UA 点位页独立分区）：OPC UA 用地址空间点选专用窗口，无批量生成
            Window window = IsOpcUa(protocolName)
                ? new OpcUaPointsWindow(viewModel)
                : new PointsWindow(viewModel);
            window.Owner = Application.Current?.MainWindow;
            window.ShowDialog();
        }
        finally
        {
            viewModel.Dispose();
        }
    }

    private static bool IsOpcUa(string protocolName) =>
        string.Equals(protocolName, "OPC UA", StringComparison.OrdinalIgnoreCase);
}
