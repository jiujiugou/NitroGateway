using System.ComponentModel;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NitroGateway.Desktop.Hosting;
using NitroGateway.Desktop.ViewModels;

namespace NitroGateway.Desktop.Views;

/// <summary>
/// 主窗口（ADR-026 D3）：关闭时先优雅停止宿主（采集 drain → 转发排空 → MQTT 关闭），
/// 排空期间窗口保持可见，结束后再退出。
/// </summary>
public partial class MainWindow : Window
{
    private readonly GatewayHost _host;
    private readonly MainViewModel _viewModel;
    private bool _shuttingDown;

    public MainWindow(GatewayHost host)
    {
        InitializeComponent();
        _host = host;
        _viewModel = host.Services.GetRequiredService<MainViewModel>();
        DataContext = _viewModel;
        Closing += OnWindowClosing;
        Closed += OnWindowClosed;
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        // ADR-027 P3-4：窗口销毁时释放 ViewModel 持有的定时器与事件退订
        _viewModel.Dispose();
    }

    private async void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_shuttingDown)
            return;

        _shuttingDown = true;
        e.Cancel = true;
        try
        {
            Title = "正在关闭（排空转发缓冲）...";
            await _host.StopAsync();
        }
        catch (Exception ex)
        {
            _host.Services.GetRequiredService<ILogger<MainWindow>>().LogError(ex, "宿主优雅关闭异常");
        }
        finally
        {
            Application.Current.Shutdown();
        }
    }
}
