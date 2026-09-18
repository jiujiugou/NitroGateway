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

        NavItems.Add(new NavItem("设备", "\uE772", devices));
        NavItems.Add(new NavItem("实时数据", "\uE9D9", realtime));
        NavItems.Add(new NavItem("告警", "\uE7BA", alarms));
        NavItems.Add(new NavItem("告警规则", "\uE8FD", alarmRules));
        NavItems.Add(new NavItem("历史查询", "\uE81C", history));
        NavItems.Add(new NavItem("设置", "\uE713", settings));

        _bridge.FrameReady += OnFrame;
        devices.DeviceCountChanged += OnDeviceCountChanged;
        DeviceCountText = devices.Items.Count.ToString(System.Globalization.CultureInfo.CurrentCulture);

        SelectedNav = NavItems[0];
    }

    partial void OnSelectedNavChanged(NavItem? value)
    {
        if (value is not null)
        {
            CurrentViewModel = value.ViewModel;
            _realtime.IsActive = ReferenceEquals(value.ViewModel, _realtime);
        }
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

    /// <summary>DevicesViewModel 刷新后同步设备数（事件已在 UI 线程触发）。</summary>
    private void OnDeviceCountChanged(object? sender, int count) => DeviceCountText = count.ToString(System.Globalization.CultureInfo.CurrentCulture);

    public void Dispose()
    {
        _bridge.FrameReady -= OnFrame;
        if (NavItems.FirstOrDefault(n => n.ViewModel is DevicesViewModel)?.ViewModel is DevicesViewModel devices)
            devices.DeviceCountChanged -= OnDeviceCountChanged;
        // 窗口关闭时随 MainViewModel 一并释放
        foreach (var nav in NavItems)
            (nav.ViewModel as IDisposable)?.Dispose();
    }
}
