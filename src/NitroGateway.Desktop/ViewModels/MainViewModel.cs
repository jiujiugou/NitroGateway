using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using NitroGateway.Desktop.Messaging;
using NitroGateway.Desktop.Services;
using NitroGateway.DeviceManagement;
using NitroGateway.Transport.MQTT;

namespace NitroGateway.Desktop.ViewModels;

/// <summary>
/// 主窗口 ViewModel：左侧导航 + 状态栏（MQTT 状态 / 缓冲积压 / 设备数）。
/// 状态栏数据来自 EventBridge 帧（D2）+ 5s 设备数刷新。
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly EventBridge _bridge;
    private readonly UiDispatcher _ui;
    private readonly IDeviceSnapshotCache _cache;
    private readonly ILogger<MainViewModel> _logger;
    private readonly DispatcherTimer _timer;

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
        HistoryViewModel history,
        SettingsViewModel settings,
        EventBridge bridge,
        UiDispatcher ui,
        IDeviceSnapshotCache cache,
        ILogger<MainViewModel> logger)
    {
        _bridge = bridge;
        _ui = ui;
        _cache = cache;
        _logger = logger;

        NavItems.Add(new NavItem("设备", devices));
        NavItems.Add(new NavItem("实时数据", realtime));
        NavItems.Add(new NavItem("告警", alarms));
        NavItems.Add(new NavItem("历史查询", history));
        NavItems.Add(new NavItem("设置", settings));

        _bridge.FrameReady += OnFrame;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _timer.Tick += async (_, _) => await RefreshDeviceCountAsync();
        _timer.Start();
        _ = RefreshDeviceCountAsync();

        SelectedNav = NavItems[0];
    }

    partial void OnSelectedNavChanged(NavItem? value)
    {
        if (value is not null)
            CurrentViewModel = value.ViewModel;
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

    private async Task RefreshDeviceCountAsync()
    {
        try
        {
            var result = await _cache.GetAllAsync();
            if (result.IsSuccess)
            {
                var count = result.Value!.Count;
                _ui.Post(() => DeviceCountText = count.ToString());
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "设备数刷新失败");
        }
    }

    public void Dispose()
    {
        _bridge.FrameReady -= OnFrame;
        _timer.Stop();
        // ADR-027 P3-4：级联释放子页面（DispatcherTimer / FrameReady 退订），
        // 窗口关闭时随 MainViewModel 一并释放
        foreach (var nav in NavItems)
            (nav.ViewModel as IDisposable)?.Dispose();
    }
}
