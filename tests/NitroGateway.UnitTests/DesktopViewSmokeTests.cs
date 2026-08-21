using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NitroGateway.Desktop.Services.Infrastructure;

using NitroGateway.Desktop.ViewModels;
using NitroGateway.Desktop.Views;
using NitroGateway.DeviceManagement;
using NitroGateway.Domain.Devices;
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

                // docs/13：批量生成窗口（协议感知提示），无 Application 实例时也可解析
                var batchWindow = new PointBatchWindow(new PointBatchEditor());
                Assert.NotNull(batchWindow);
                batchWindow.Measure(new Size(800, 600));
                batchWindow.Arrange(new Rect(0, 0, 800, 600));
                batchWindow.UpdateLayout();

                // ADR-043：告警规则编辑窗口（设备/点位级联表单），无 Application 实例时也可解析
                var alarmRuleWindow = new AlarmRuleEditorWindow(new AlarmRuleEditor(Array.Empty<Device>()));
                Assert.NotNull(alarmRuleWindow);
                alarmRuleWindow.Measure(new Size(800, 600));
                alarmRuleWindow.Arrange(new Rect(0, 0, 800, 600));
                alarmRuleWindow.UpdateLayout();

                var services = new ServiceCollection();
                services.AddScoped<IPointManager>(_ => new StubPointManager());
                using var provider = services.BuildServiceProvider();
                var pointsVm = new PointsViewModel(
                    Guid.NewGuid(), "测试设备", "Modbus",
                    provider.GetRequiredService<IServiceScopeFactory>(),
                    new StubDeviceDialogService(), new StubConfigSyncOutboxStore(),
                    new StubCsvFileService(), new PointBatchService(NullLogger<PointBatchService>.Instance),
                    NullLogger<PointsViewModel>.Instance);
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

    [Fact]
    public void ListViews_initialize_on_sta()
    {
        Exception? error = null;

        var thread = new Thread(() =>
        {
            try
            {
                // ADR-037 S1/S3：列表视图合并共享样式（含 BoolToVis/状态色令牌/空态叠加层），
                // 无 Application 实例时 StaticResource 也必须可解析（DataContext 留空仅验证模板加载）
                var views = new System.Windows.FrameworkElement[]
                {
                    new DevicesView(),
                    new AlarmsView(),
                    new AlarmRulesView(),
                    new HistoryView(),
                    new SettingsView(),
                    new StartupWindow()
                };
                foreach (var view in views)
                {
                    Assert.NotNull(view);
                    view.Measure(new Size(800, 600));
                    view.Arrange(new Rect(0, 0, 800, 600));
                    view.UpdateLayout();
                }
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
