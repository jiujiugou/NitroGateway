using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NitroGateway.Desktop.Hosting;
using NitroGateway.Desktop.Views;
using System.Windows;
using System.Windows.Threading;

namespace NitroGateway.Desktop;

/// <summary>
/// WPF 应用入口（ADR-026）。<c>App.xaml</c> 未声明 <c>StartupUri</c>，因此进程启动与退出
/// 完全由本类编排，职责如下：
/// <list type="number">
/// <item>单实例 Mutex（D6）：现场只允许一个采集进程，避免两个进程双写同一 SQLite。</item>
/// <item>全局异常兜底（D7）：AppDomain + Dispatcher 两级未处理异常，记日志并提示，不闪退。</item>
/// <item>启动可视化：先显示 <see cref="StartupWindow"/>，避免迁移与服务启动期间白屏无反馈。</item>
/// <item>宿主生命周期：<see cref="GatewayHost"/> 的创建、启动，以及异常的兜底停止与释放。</item>
/// </list>
/// 正常关闭路径的设计是 <c>MainWindow.Closing</c> 触发 drain；本类 <see cref="OnExit"/> 只做兜底。
/// </summary>
public partial class App : Application, IDisposable
{
    /// <summary>命名 Mutex 名称：现场只允许一个采集进程，防止双写同一 SQLite。</summary>
    private const string SingleInstanceMutexName = "NitroGateway.Desktop.SingleInstance";

    /// <summary>
    /// 单实例互斥体句柄。<see cref="OnStartup"/> 创建，<see cref="Dispose"/> 释放。
    /// 无论是否抢到所有权，该对象都会创建；仅在 <see cref="_ownsMutex"/> 为 <c>true</c> 时才需
    /// <c>ReleaseMutex</c>，但 <c>Dispose</c> 应无条件执行以释放句柄。
    /// </summary>
    private Mutex? _singleInstanceMutex;

    /// <summary>
    /// 本进程是否为互斥体的所有者：<c>true</c> 表示首个实例、可继续启动；
    /// <c>false</c> 表示已有实例在运行，本进程应提示后退出。
    /// </summary>
    private bool _ownsMutex;

    /// <summary>
    /// 桌面宿主（DI 容器 + 全部后台服务）。<see cref="OnStartup"/> 中创建，失败路径可能为 <c>null</c>；
    /// <see cref="OnExit"/> 与 <see cref="Dispose"/> 据此判断是否需要停止/释放。
    /// </summary>
    private GatewayHost? _host;

    /// <summary>
    /// 应用日志器。依赖宿主 DI，故只有在 <see cref="GatewayHost.Create"/> 成功后才能取得；
    /// 宿主构建阶段抛异常时该字段为 <c>null</c>，此时只能靠 UI 提示反馈错误。
    /// </summary>
    private ILogger<App>? _logger;

    /// <summary>
    /// 应用启动入口（重写 <see cref="Application.OnStartup"/>）。执行顺序：
    /// <list type="number">
    /// <item>抢占单实例 Mutex；未抢到则提示并立即退出。</item>
    /// <item>注册 AppDomain 与 Dispatcher 两级未处理异常处理器（D7）。</item>
    /// <item>先显示启动窗，再构建并启动宿主（含迁移与全部后台服务）。</item>
    /// <item>启动成功则创建并显示 <see cref="MainWindow"/>、关闭启动窗；
    ///       失败则在启动窗内展示错误并等待用户关闭。</item>
    /// </list>
    /// 说明：本方法签名要求 <c>async void</c>（无法返回 <c>Task</c>），
    /// 故通过 <c>try/catch</c> 保证启动异常不外溢。
    /// </summary>
    /// <param name="e">启动参数，<see cref="GatewayHost.Create"/> 会转发给配置系统。</param>
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // D6：命名 Mutex 做进程互斥。initiallyOwned=true 表示尝试立即取得所有权，
        // 由 out 参数回传结果；抢不到说明已有实例在运行。
        _singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out _ownsMutex);
        if (!_ownsMutex)
        {
            MessageBox.Show("NitroGateway 现场采集端已在运行。", "NitroGateway 现场采集端",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // D7：注册全局异常兜底。AppDomain 捕获非 UI/CLR 级异常，Dispatcher 捕获 UI 线程异常。
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        // 启动失败在启动窗内提示（迁移+服务启动可能数秒，避免白屏无反馈）
        var splash = new StartupWindow();
        splash.Show();
        try
        {
            // Host 构建（DI 组装）→ 取日志器 → 启动（迁移 + 后台服务）。
            _host = GatewayHost.Create(e.Args);
            _logger = _host.Services.GetRequiredService<ILogger<App>>();
            await _host.StartAsync();
        }
        catch (Exception ex)
        {
            // 启动失败：记录日志（若宿主已建好才有日志器），并在启动窗展示错误。
            // return 后由用户点击「关闭」触发退出（ShutdownMode=OnLastWindowClose）。
            _logger?.LogError(ex, "宿主启动失败");
            splash.ViewModel.ShowError(ex.Message);
            return;
        }

        // 启动成功：以 MainWindow 作为主窗口，显示后关闭启动窗。
        var mainWindow = new MainWindow(_host);
        MainWindow = mainWindow;
        mainWindow.Show();
        splash.Close();
    }

    /// <summary>
    /// 应用退出入口（重写 <see cref="Application.OnExit"/>）。
    /// 正常关闭时 <c>MainWindow.Closing</c> 已完成 drain，此处 <c>StopAsync</c> 幂等快速返回；
    /// 若未走正常路径（如启动失败后用户关闭启动窗），则在此兜底停止并释放宿主。
    /// </summary>
    /// <param name="e">退出事件参数。</param>
    protected override void OnExit(ExitEventArgs e)
    {
        // 兜底关闭：StopAsync 已在 MainWindow.Closing 完成，这里只释放。
        // 释放放到线程池并设超时——绝不在 UI 线程上无限 GetResult()，
        // 否则某个 Dispose（MQTT 断开/后台循环）卡住会让进程永不退出、独占单实例锁。
        if (_host is not null)
        {
            var host = _host;
            bool completed;
            try
            {
                completed = Task.Run(async () =>
                {
                    try { await host.DisposeAsync(); }
                    catch (Exception ex) { _logger?.LogError(ex, "退出时宿主释放异常"); }
                }).Wait(TimeSpan.FromSeconds(3));
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "退出时宿主释放异常");
                completed = false;
            }

            if (!completed)
            {
                _logger?.LogWarning("宿主释放超时，强制退出进程");
                Dispose();
                Environment.Exit(0);
            }
        }

        // 释放单实例 Mutex，再交给基类完成退出。
        Dispose();
        base.OnExit(e);
    }

    /// <summary>
    /// 释放单实例 Mutex（CA1001）。在 <see cref="OnExit"/> 末尾调用；
    /// 若本进程并非互斥体所有者，则无需 <c>ReleaseMutex</c>。
    /// </summary>
    public void Dispose()
    {
        if (_ownsMutex) { _singleInstanceMutex?.ReleaseMutex(); _singleInstanceMutex?.Dispose(); }
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// AppDomain 级未处理异常处理器：仅记录日志。此类异常通常发生在非 UI 线程，
    /// 且 CLR 即将终止进程，无法通过 UI 挽救，仅保证现场有日志可查。
    /// </summary>
    /// <param name="sender">事件源（当前 AppDomain）。</param>
    /// <param name="e">异常参数，<see cref="UnhandledExceptionEventArgs.ExceptionObject"/> 携带异常对象。</param>
    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            _logger?.LogError(ex, "AppDomain 未处理异常");
    }

    /// <summary>
    /// Dispatcher（UI 线程）未处理异常处理器：记录日志、弹出非阻塞警告，并标记为已处理，
    /// 使应用不闪退（D7），采集与转发等后台服务保持运行。
    /// </summary>
    /// <param name="sender">事件源（当前 Dispatcher）。</param>
    /// <param name="e">异常参数；<c>Handled=true</c> 阻止异常继续上抛导致进程退出。</param>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _logger?.LogError(e.Exception, "UI 线程未处理异常");
        MessageBox.Show($"发生未处理异常：{e.Exception.Message}", "NitroGateway 现场采集端",
            MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true; // D7：非阻塞提示，不闪退
    }
}
