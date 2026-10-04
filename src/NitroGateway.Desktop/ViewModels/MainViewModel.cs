using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using NitroGateway.Desktop.Messaging;
using NitroGateway.Desktop.Services.Infrastructure;
using NitroGateway.Transport.MQTT;

namespace NitroGateway.Desktop.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly EventBridge _bridge;
    private readonly UiDispatcher _ui;
    private readonly RealtimeViewModel _realtime;
    private readonly IReadOnlyList<DevicesViewModel> _deviceLists;
    private readonly NavNode _defaultNav;

    /// <summary>侧栏导航树：一级「协议」目录统辖两个二级设备分区，其余为顶级页面项。</summary>
    public ObservableCollection<NavNode> NavTree { get; } = [];

    [ObservableProperty] private NavNode? _selectedNav;
    [ObservableProperty] private ObservableObject? _currentViewModel;

    [ObservableProperty] private string _mqttStateText = "未连接";
    [ObservableProperty] private string _bufferBacklogText = "—";
    [ObservableProperty] private string _deviceCountText = "—";
    [ObservableProperty] private string _statusText = "";

    public MainViewModel(
        DashboardViewModel dashboard,
        IDevicesViewModelFactory deviceLists,
        RealtimeViewModel realtime,
        AlarmsViewModel alarms,
        AlarmRulesViewModel alarmRules,
        HistoryViewModel history,
        SettingsViewModel settings,
        MetricsViewModel metrics,
        EventBridge bridge,
        UiDispatcher ui)
    {
        _bridge = bridge;
        _ui = ui;
        _realtime = realtime;

        // 协议分区（对齐 web App.vue 的「设备管理」分组）：Modbus/S7 与 OPC UA 各自独立列表，
        // 复用同一套 DevicesView，由 DeviceListScope 决定过滤与列显隐。
        var genericDevices = deviceLists.Create(DeviceListScope.Generic);
        var opcUaDevices = deviceLists.Create(DeviceListScope.OpcUa);
        _deviceLists = [genericDevices, opcUaDevices];

        // 导航（对齐 web）：仪表盘 → 一级「协议」目录（统辖两个二级分区）→ 其余顶级页面
        _defaultNav = NavNode.Page("仪表盘", "\uE80F", dashboard);
        NavTree.Add(_defaultNav);
        NavTree.Add(NavNode.Group("协议", "\uE968")
            .With(
                NavNode.Page(DeviceListScope.Generic.Title, "\uE772", genericDevices),
                NavNode.Page(DeviceListScope.OpcUa.Title, "\uE774", opcUaDevices)));
        NavTree.Add(NavNode.Page("实时数据", "\uE9D9", realtime));
        NavTree.Add(NavNode.Page("告警", "\uE7BA", alarms));
        NavTree.Add(NavNode.Page("告警规则", "\uE8FD", alarmRules));
        NavTree.Add(NavNode.Page("历史查询", "\uE81C", history));
        NavTree.Add(NavNode.Page("系统监控", "\uE9D9", metrics));
        NavTree.Add(NavNode.Page("设置", "\uE713", settings));

        foreach (var node in AllNodes())
            node.SelectionRequested = OnNavSelectionRequested;

        _bridge.FrameReady += OnFrame;
        foreach (var devices in _deviceLists)
            devices.DeviceCountChanged += OnDeviceCountChanged;
        DeviceCountText = TotalDeviceCount();

        OnNavSelectionRequested(_defaultNav);
    }

    /// <summary>
    /// 导航项被点击（View层入口）：一级「协议」目录只切换展开态、不改内容；页面项切换内容并单高亮。
    /// </summary>
    public void SelectNav(NavNode? node)
    {
        if (node is not null)
            OnNavSelectionRequested(node);
    }

    /// <summary>
    /// 导航选中：一级目录只切换展开态且不参与高亮；页面项高亮自身并切换内容区。
    /// 由 <see cref="NavNode.IsSelected"/> 状态驱动（View 层经 DataTrigger 消费，避免受 TreeView 选中语义影响）。
    /// </summary>
    private void OnNavSelectionRequested(NavNode node)
    {
        if (node.IsGroup)
        {
            node.IsSelected = false;               // 目录不可选中
            node.IsExpanded = !node.IsExpanded;    // 点击标题仅展开/收起
            return;
        }

        foreach (var leaf in LeafNodes())
            leaf.IsSelected = ReferenceEquals(leaf, node);

        SelectedNav = node;
    }

    private IEnumerable<NavNode> AllNodes()
    {
        foreach (var node in NavTree)
        {
            yield return node;
            foreach (var child in node.Children)
                yield return child;
        }
    }

    private IEnumerable<NavNode> LeafNodes() => AllNodes().Where(n => !n.IsGroup);

    partial void OnSelectedNavChanged(NavNode? value)
    {
        if (value?.ViewModel is not { } viewModel)
            return;

        CurrentViewModel = viewModel;
        _realtime.IsActive = ReferenceEquals(viewModel, _realtime);
    }

    public void SetRealtimeVisible(bool visible)
    {
        if (!visible)
            _realtime.IsActive = false;
        else
            _realtime.IsActive = ReferenceEquals(SelectedNav?.ViewModel, _realtime);
    }

    private void OnFrame(UiFrame frame)
    {
        if (frame.MqttState is null && frame.BufferBacklog is null)
            return;

        _ui.Post(() =>
        {
            if (frame.MqttState is MqttConnectionState state)
                MqttStateText = state switch
                {
                    MqttConnectionState.Connected => "已连接",
                    MqttConnectionState.Connecting => "连接中",
                    MqttConnectionState.Reconnecting => "重连中",
                    MqttConnectionState.Faulted => "故障",
                    MqttConnectionState.Disabled => "MQTT 已关闭",
                    _ => "未连接"
                };

            if (frame.BufferBacklog is int backlog)
                BufferBacklogText = backlog.ToString("N0", System.Globalization.CultureInfo.CurrentCulture);
        });
    }

    /// <summary>设备列表刷新后同步设备数（两个协议分区求和；事件已在 UI 线程触发）。</summary>
    private void OnDeviceCountChanged(object? sender, int count) => DeviceCountText = TotalDeviceCount();

    private string TotalDeviceCount() =>
        _deviceLists.Sum(v => v.Items.Count).ToString(System.Globalization.CultureInfo.CurrentCulture);

    public void Dispose()
    {
        _bridge.FrameReady -= OnFrame;
        foreach (var devices in _deviceLists)
            devices.DeviceCountChanged -= OnDeviceCountChanged;
        // 窗口关闭时随 MainViewModel 一并释放
        foreach (var node in AllNodes())
            (node.ViewModel as IDisposable)?.Dispose();
    }
}
