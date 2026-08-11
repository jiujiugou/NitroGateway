using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NitroGateway.Desktop.Services;
using NitroGateway.Desktop.ViewModels;
using NitroGateway.Desktop.Views;
using NitroGateway.DeviceManagement;
using Xunit;

namespace NitroGateway.UnitTests;

/// <summary>
/// ADR-026：WPF 视图冒烟——在 STA 线程实例化含 LiveCharts2 图表的视图，
/// 验证 SkiaSharp/LiveCharts 依赖在运行时可用（构建期仅有 NU1701 兼容性警告）。
/// 注意：不创建 Application（ADR-027 测试稳定性）——Application.Current 是 AppDomain
/// 级单例，遗留实例会让其他测试的 UiDispatcher.Post 改走 Dispatcher 通道导致竞态测试挂起。
/// </summary>
public sealed class DesktopViewSmokeTests
{
    [Fact]
    public void RealtimeView_initializes_with_chart()
    {
        Exception? error = null;

        var thread = new Thread(() =>
        {
            try
            {
                var view = new RealtimeView();
                Assert.NotNull(view);

                // 强制布局，触发图表控件的实际创建
                view.Measure(new Size(800, 600));
                view.Arrange(new Rect(0, 0, 800, 600));
                view.UpdateLayout();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(30));

        Assert.Null(error);
        Assert.False(thread.IsAlive);
    }
    [Fact]
    public void DeviceConfigWindows_initialize_on_sta()
    {
        Exception? error = null;

        var thread = new Thread(() =>
        {
            try
            {
                var deviceWindow = new DeviceEditorWindow(new DeviceEditor());
                Assert.NotNull(deviceWindow);
                deviceWindow.Measure(new Size(800, 600));
                deviceWindow.Arrange(new Rect(0, 0, 800, 600));
                deviceWindow.UpdateLayout();

                var pointWindow = new PointEditorWindow(new PointEditor());
                Assert.NotNull(pointWindow);
                pointWindow.Measure(new Size(800, 600));
                pointWindow.Arrange(new Rect(0, 0, 800, 600));
                pointWindow.UpdateLayout();

                var services = new ServiceCollection();
                services.AddScoped<IPointManager>(_ => new StubPointManager());
                using var provider = services.BuildServiceProvider();
                var pointsVm = new PointsViewModel(
                    Guid.NewGuid(), "测试设备",
                    provider.GetRequiredService<IServiceScopeFactory>(),
                    new StubDeviceDialogService(), NullLogger<PointsViewModel>.Instance);
                var pointsWindow = new PointsWindow(pointsVm);
                Assert.NotNull(pointsWindow);
                pointsWindow.Measure(new Size(800, 600));
                pointsWindow.Arrange(new Rect(0, 0, 800, 600));
                pointsWindow.UpdateLayout();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(30));

        Assert.Null(error);
        Assert.False(thread.IsAlive);
    }
}
