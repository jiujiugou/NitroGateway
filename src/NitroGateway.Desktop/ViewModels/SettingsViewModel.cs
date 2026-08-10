using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Configuration;
using NitroGateway.Desktop.Messaging;
using NitroGateway.Desktop.Hosting;
using NitroGateway.Desktop.Services;
using NitroGateway.Transport.MQTT;

namespace NitroGateway.Desktop.ViewModels;

/// <summary>
/// 设置/状态页（ADR-026 D9 现场可视性）：MQTT 连接状态、缓冲水位、
/// 本地库路径与采集配置只读展示；配置编辑属 P2。
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject, IDisposable
{
    private readonly EventBridge _bridge;
    private readonly UiDispatcher _ui;

    [ObservableProperty] private string _mqttBroker = "";
    [ObservableProperty] private string _mqttClientId = "";
    [ObservableProperty] private string _mqttStateText = "未连接";
    [ObservableProperty] private string _bufferBacklogText = "—";
    [ObservableProperty] private string _databasePath = "";
    [ObservableProperty] private string _logDirectory = "";
    [ObservableProperty] private string _collectionInterval = "";
    [ObservableProperty] private string _forwarderInterval = "";

    public SettingsViewModel(
        MqttConnectionOptions mqtt,
        IConfiguration configuration,
        EventBridge bridge,
        UiDispatcher ui)
    {
        _bridge = bridge;
        _ui = ui;

        MqttBroker = $"{mqtt.Host}:{mqtt.Port}" + (mqtt.UseTls ? " (TLS)" : "");
        MqttClientId = mqtt.ClientId ?? "—";
        DatabasePath = configuration["Persistence:ConnectionString"] ?? "";
        // ADR-027 P3-3：按 Name 定位 File sink，避免 WriteTo 增删后索引错位
        LogDirectory = Path.GetDirectoryName(configuration[DesktopPathConfig.FileSinkPathKey(configuration)]) ?? "";
        CollectionInterval = configuration["Collection:IntervalMs"] ?? "";
        ForwarderInterval = configuration["Forwarder:IntervalMs"] ?? "";

        _bridge.FrameReady += OnFrame;
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

    public void Dispose() => _bridge.FrameReady -= OnFrame;
}
