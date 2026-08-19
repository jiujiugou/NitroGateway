using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using NitroGateway.Desktop.Messaging;
using NitroGateway.Desktop.Services.Infrastructure;
using NitroGateway.Transport.MQTT;

namespace NitroGateway.Desktop.ViewModels;

/// <summary>
/// 主窗口 ViewModel：左侧导航 + 状态栏（MQTT 状态 / 缓冲积压 / 设备数）。
/// 状态栏数据来自 EventBridge 帧（D2）；设备数复用 DevicesViewModel 的刷新事件（ADR-037 S11）。
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly EventBridge _bridge;
    private readonly UiDispatcher _ui;
    private readonly RealtimeViewModel _realtime;

    public ObservableCollection<NavItem> NavItems { get; } = [];

    [ObservableProperty] private NavItem? _selectedNav;
    [ObservableProperty] private ObservableObject? _currentViewModel;

    [ObservableProperty] private string _mqttStateText = "未连接";
    [ObservableProperty] private string _bufferBacklogText = "—";
    [ObservableProperty] private string _deviceCountText = "—";
    [ObservableProperty] private string _statusText = "";

    public MainViewModel(
        DevicesViewModel devices,
        RealtimeViewModel realtime,
        AlarmsViewModel alarms,
        AlarmRulesViewModel alarmRules,
        HistoryViewModel history,
        SettingsViewModel settings,
        EventBridge bridge,
        UiDispatcher ui)
    {
        _bridge = bridge;
        _ui = ui;
        _realtime = realtime;

        // ADR-037 S10：导航项带 Segoe MDL2 Assets 图标字形
        NavItems.Add(new NavItem("设备", "\uE772", devices));
        NavItems.Add(new NavItem("实时数据", "\uE9D9", realtime));
        NavItems.Add(new NavItem("告警", "\uE7BA", alarms));
        // ADR-043：告警规则管理页（紧邻「告警」，Segoe MDL2 BulletedList）
        NavItems.Add(new NavItem("告警规则", "\uE8FD", alarmRules));
        NavItems.Add(new NavItem("历史查询", "\uE81C", history));
        NavItems.Add(new NavItem("设置", "\uE713", settings));

        _bridge.FrameReady += OnFrame;
        // ADR-037 S11：设备数并入 DevicesViewModel 刷新事件（同一 5s 节奏，不再重复查询目录）
        devices.DeviceCountChanged += OnDeviceCountChanged;
        DeviceCountText = devices.Items.Count.ToString();

        SelectedNav = NavItems[0];
    }

    partial void OnSelectedNavChanged(NavItem? value)
    {
        if (value is not null)
        {
            CurrentViewModel = value.ViewModel;
            // ADR-045 P1：仅实时页激活曲线，其余页全部暂停（后台不再重绘/增长）
            _realtime.IsActive = ReferenceEquals(value.ViewModel, _realtime);
        }
    }

    /// <summary>
    /// 窗口可见性变化（ADR-045 P1）：最小化时暂停实时曲线（背景不重绘），
    /// 还原时仅在当前就在实时页时恢复。
    /// </summary>
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
                    _ => "未连接"
                };

            if (frame.BufferBacklog is int backlog)
                BufferBacklogText = backlog.ToString("N0");
        });
    }

    /// <summary>DevicesViewModel 刷新后同步设备数（事件已在 UI 线程触发）。</summary>
    private void OnDeviceCountChanged(object? sender, int count) => DeviceCountText = count.ToString();

    public void Dispose()
    {
        _bridge.FrameReady -= OnFrame;
        if (NavItems.FirstOrDefault(n => n.ViewModel is DevicesViewModel)?.ViewModel is DevicesViewModel devices)
            devices.DeviceCountChanged -= OnDeviceCountChanged;
        // ADR-027 P3-4：级联释放子页面（DispatcherTimer / FrameReady 退订），
        // 窗口关闭时随 MainViewModel 一并释放
        foreach (var nav in NavItems)
            (nav.ViewModel as IDisposable)?.Dispose();
    }
}
