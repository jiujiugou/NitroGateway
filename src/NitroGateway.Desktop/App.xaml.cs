using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NitroGateway.Desktop.Hosting;
using NitroGateway.Desktop.Views;
using System.Windows;
using System.Windows.Threading;

namespace NitroGateway.Desktop;

/// <summary>
/// WPF 应用入口（ADR-026）。负责：单实例 Mutex（D6）、全局异常兜底（D7）、
/// 宿主启动（迁移 + 后台服务）与 MainWindow 创建；关闭走 MainWindow.Closing 的 drain。
/// </summary>
public partial class App : Application
{
    /// <summary>命名 Mutex：现场只允许一个采集进程，防止双写同一 SQLite。</summary>
    private const string SingleInstanceMutexName = "NitroGateway.Desktop.SingleInstance";

    private Mutex? _singleInstanceMutex;
    private bool _ownsMutex;
    private GatewayHost? _host;
    private ILogger<App>? _logger;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out _ownsMutex);
        if (!_ownsMutex)
        {
            MessageBox.Show("NitroGateway 现场采集端已在运行。", "NitroGateway 现场采集端",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        // ADR-037 S8：先显示启动反馈窗口，宿主就绪后再切主窗口；
        // 启动失败在启动窗内提示（迁移+服务启动可能数秒，避免白屏无反馈）
        var splash = new StartupWindow();
        splash.Show();
        try
        {
            _host = GatewayHost.Create(e.Args);
            _logger = _host.Services.GetRequiredService<ILogger<App>>();
            await _host.StartAsync();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "宿主启动失败");
            splash.ShowError(ex.Message);
            return; // 用户点「关闭」退出（ShutdownMode=OnLastWindowClose）
        }

        var mainWindow = new MainWindow(_host);
        MainWindow = mainWindow;
        mainWindow.Show();
        splash.Close();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 兜底关闭：MainWindow.Closing 已正常 drain 时，此处 StopAsync 幂等快速返回。
        if (_host is not null)
        {
            try { _host.StopAsync().GetAwaiter().GetResult(); }
            catch (Exception ex) { _logger?.LogError(ex, "退出时宿主停止异常"); }
            finally { _host.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        }

        if (_ownsMutex) { _singleInstanceMutex?.ReleaseMutex(); _singleInstanceMutex?.Dispose(); }
        base.OnExit(e);
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            _logger?.LogError(ex, "AppDomain 未处理异常");
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _logger?.LogError(e.Exception, "UI 线程未处理异常");
        MessageBox.Show($"发生未处理异常：{e.Exception.Message}", "NitroGateway 现场采集端",
            MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true; // D7：非阻塞提示，不闪退
    }
}
