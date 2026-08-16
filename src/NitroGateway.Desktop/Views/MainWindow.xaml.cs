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
        // ADR-045 P1：窗口最小化时暂停实时曲线（背景不重绘），还原时恢复
        StateChanged += OnWindowStateChanged;
        // ADR-051：窗口失焦（切到其他应用）时暂停实时刷新，避免后台 5fps 抢占 UI 线程；
        // 切回时 UI 线程空闲，恢复即时跟手（原实现失焦仍全速刷，切回要追赶积压 + 整窗重绘）
        Deactivated += OnWindowDeactivated;
        Activated += OnWindowActivated;
    }

    /// <summary>窗口最小化 → 暂停实时曲线；还原 → 仅当当前在实时页时恢复（ADR-045 P1）。</summary>
    private void OnWindowStateChanged(object? sender, EventArgs e)
        => _viewModel.SetRealtimeVisible(WindowState != WindowState.Minimized);

    /// <summary>
    /// 窗口失焦（切到其他应用/对话框，ADR-051）：暂停实时页刷新——后台不再以 5fps 逐帧刷表
    /// 抢占 UI 线程（原实现在失焦时仍全速刷，用户从其他窗口切回时卡顿 + 切回后仍卡一阵）。
    /// 最小化已由 <see cref="OnWindowStateChanged"/> 暂停，这里跳过以免重复。
    /// </summary>
    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
            return;
        _viewModel.SetRealtimeVisible(false);
    }

    /// <summary>
    /// 窗口重新激活（ADR-051）：恢复实时页刷新。失焦期间后台已暂停、UI 线程空闲，恢复即时跟手；
    /// 实时页由 <see cref="RealtimeViewModel.OnIsActiveChanged"/> 用内存缓存一次补齐表格行。
    /// 用 WindowState 守卫：还原时可能 Activated 先于 StateChanged，避免在仍最小化时恢复刷新。
    /// </summary>
    private void OnWindowActivated(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
            return;
        _viewModel.SetRealtimeVisible(true);
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
