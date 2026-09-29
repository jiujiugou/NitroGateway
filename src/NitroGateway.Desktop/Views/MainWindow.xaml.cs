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
