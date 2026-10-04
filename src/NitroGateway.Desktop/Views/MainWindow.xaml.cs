using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
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
        StateChanged += OnWindowStateChanged;
        // 切回时 UI 线程空闲，恢复即时跟手（原实现失焦仍全速刷，切回要追赶积压 + 整窗重绘）
        Deactivated += OnWindowDeactivated;
        Activated += OnWindowActivated;
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
        => _viewModel.SetRealtimeVisible(WindowState != WindowState.Minimized);

    /// <summary>
    /// 导航项点击：交给 ViewModel 决定（一级「协议」目录只展开/收起，不切换内容）。
    /// 选中高亮由 <c>NavNode.IsSelected</c> 经 DataTrigger 驱动，故此处不依赖 TreeView 自身的选中语义。
    /// </summary>
    private void OnNavNodeSelected(object sender, RoutedEventArgs e)
    {
        if (sender is not TreeViewItem { DataContext: NavNode node } item)
            return;

        _viewModel.SelectNav(node);

        if (node.IsGroup)
        {
            // 目录不可选中：撤销容器选中，使再次点击仍触发 Selected（从而可反复展开/收起）
            item.Dispatcher.BeginInvoke(new Action(() => item.IsSelected = false), DispatcherPriority.Input);
        }
    }

    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
            return;
        _viewModel.SetRealtimeVisible(false);
    }

    private void OnWindowActivated(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
            return;
        _viewModel.SetRealtimeVisible(true);
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _viewModel.Dispose();
    }

    private async void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_shuttingDown)
            return;

        _shuttingDown = true;
        e.Cancel = true;
        var logger = _host.Services.GetRequiredService<ILogger<MainWindow>>();

        // 硬看门狗：任何路径卡住超过 10s 一律强杀进程，避免窗口已关但进程残留、独占单实例锁。
        _ = ForceExitAfterAsync(TimeSpan.FromSeconds(10));

        try
        {
            Title = "正在关闭（排空转发缓冲）...";
            // 最多等 5 秒优雅关停；慢 Broker 排空/卡死的子系统一律到此为止，绝不让进程滞留。
            var stopTask = _host.StopAsync();
            if (await Task.WhenAny(stopTask, Task.Delay(TimeSpan.FromSeconds(5))) != stopTask)
            {
                logger.LogWarning("宿主关停超过 5 秒，强制退出进程");
                Environment.Exit(0);
            }

            try { await stopTask; }
            catch (Exception ex) { logger.LogError(ex, "宿主优雅关闭异常"); }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "宿主优雅关闭异常");
        }
        finally
        {
            Application.Current.Shutdown();
        }
    }

    /// <summary>延时后强制退出进程（关停看门狗）。正常运行路径下进程会先于该延时退出。</summary>
    private static async Task ForceExitAfterAsync(TimeSpan timeout)
    {
        await Task.Delay(timeout).ConfigureAwait(false);
        Environment.Exit(0);
    }
}
