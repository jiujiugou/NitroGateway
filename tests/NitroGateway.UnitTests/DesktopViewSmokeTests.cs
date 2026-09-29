using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NitroGateway.Desktop.Services.Connectivity;
using NitroGateway.Desktop.Services.Infrastructure;

using NitroGateway.Desktop.ViewModels;
using NitroGateway.Desktop.Views;
using NitroGateway.DeviceManagement;
using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Protocols;
using NitroGateway.Shared;
using Xunit;

namespace NitroGateway.UnitTests;

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

                // 协议分区：OPC UA 点位窗口（地址空间点选）与其「左树右表单」编辑器窗口
                var opcUaPointsWindow = new OpcUaPointsWindow(pointsVm);
                Assert.NotNull(opcUaPointsWindow);
                opcUaPointsWindow.Measure(new Size(800, 600));
                opcUaPointsWindow.Arrange(new Rect(0, 0, 800, 600));
                opcUaPointsWindow.UpdateLayout();

                var opcUaEditorVm = new OpcUaPointEditorViewModel(
                    Guid.NewGuid(), new PointEditor { ProtocolName = "OPC UA" }, new EmptyNodeBrowser());
                var opcUaEditorWindow = new OpcUaPointEditorWindow(opcUaEditorVm);
                Assert.NotNull(opcUaEditorWindow);
                opcUaEditorWindow.Measure(new Size(800, 600));
                opcUaEditorWindow.Arrange(new Rect(0, 0, 800, 600));
                opcUaEditorWindow.UpdateLayout();
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
                // 无 Application 实例时 StaticResource 也必须可解析（DataContext 留空仅验证模板加载）
                var views = new System.Windows.FrameworkElement[]
                {
                    new DashboardView(),
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

    /// <summary>冒烟用空浏览器：不访问网络，返回空根层。</summary>
    private sealed class EmptyNodeBrowser : IOpcUaNodeBrowser
    {
        public Task<OperationResult<IReadOnlyList<BrowseNode>>> BrowseAsync(
            Guid deviceId, string parentNodeId, CancellationToken ct = default) =>
            Task.FromResult(OperationResult<IReadOnlyList<BrowseNode>>.Success(Array.Empty<BrowseNode>()));
    }
}
