using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Configuration;
using NitroGateway.Desktop.Messaging;
using NitroGateway.Desktop.Hosting;
using NitroGateway.Desktop.Services.Dialogs;
using NitroGateway.Desktop.Services.Infrastructure;
using NitroGateway.Desktop.Services.Settings;
using NitroGateway.Desktop.Services.Sync;

using NitroGateway.Transport.MQTT;
using NitroGateway.Shared;
using NitroGateway.Storage.Buffer;

namespace NitroGateway.Desktop.ViewModels;

/// <summary>
/// 设置/状态页（ADR-026 D9 现场可视性 + ADR-033 阶段 2）：MQTT 连接状态、缓冲水位、
/// 本地库路径与采集配置只读展示；中心地址/Token 输入与「从中心导入」（以中心为准重置本地）。
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject, IDisposable
{
    private readonly EventBridge _bridge;
    private readonly UiDispatcher _ui;
    private readonly ICenterConfigClient _centerClient;
    private readonly ICenterConfigImporter _importer;
    private readonly ICenterSyncSettingsStore _settingsStore;
    private readonly IConfigSyncOutboxStore _outbox;
    private readonly IDeviceDialogService _dialogs;
    private readonly IConfiguration _configuration;
    private readonly ISiteIdProvider _siteIdProvider;
    private readonly IDesktopSettingsStore _logSettingsStore;
    private readonly IForwardMqttToggle _forwardMqttToggle;

    [ObservableProperty] private string _mqttBroker = "";
    [ObservableProperty] private string _mqttClientId = "";
    [ObservableProperty] private string _mqttStateText = "未连接";
    [ObservableProperty] private string _bufferBacklogText = "-";
    [ObservableProperty] private string _databasePath = "";
    [ObservableProperty] private string _logDirectory = "";
    [ObservableProperty] private string _logDirectoryStatus = "";
    [ObservableProperty] private string _collectionInterval = "";
    [ObservableProperty] private string _forwarderInterval = "";

    /// <summary>MQTT 上云转发开关（ADR-059）：即时生效并持久化，重启保持。</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ToggleForwardMqttCommand))]
    private bool _forwardMqttEnabled = true;

    /// <summary>MQTT 上云转发开关保存状态提示</summary>
    [ObservableProperty] private string _forwardMqttStatus = "";

    /// <summary>MQTT 上云转发开关保存进行中：期间禁用开关，防重复点击</summary>
    [ObservableProperty] private bool _isSavingForwardMqtt;

    /// <summary>站点标识（ADR-036）：生效值展示，可编辑/重新生成；保存后重启生效</summary>
    [ObservableProperty] private string _siteId = "";

    /// <summary>站点标识保存/生成操作的状态提示</summary>
    [ObservableProperty] private string _siteIdStatus = "";

    /// <summary>中心 Webapi 地址（持久化到 %LocalAppData%\NitroGateway\center-sync.json）</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ImportFromCenterCommand))]
    private string _centerUrl = "";

    /// <summary>中心 JWT Token（持久化到本机设置文件）</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ImportFromCenterCommand))]
    private string _centerToken = "";

    /// <summary>导入操作状态提示（成功/失败/取消）</summary>
    [ObservableProperty] private string _importStatusText = "";

    /// <summary>导入进行中标志：期间禁用按钮，防重复点击</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ImportFromCenterCommand))]
    private bool _isImporting;

    public SettingsViewModel(
        MqttConnectionOptions mqtt,
        IConfiguration configuration,
        EventBridge bridge,
        UiDispatcher ui,
        ICenterConfigClient centerClient,
        ICenterConfigImporter importer,
        ICenterSyncSettingsStore settingsStore,
        IConfigSyncOutboxStore outbox,
        IDeviceDialogService dialogs,
        ISiteIdProvider siteIdProvider,
        IDesktopSettingsStore logSettingsStore,
        IForwardMqttToggle forwardMqttToggle)
    {
        _bridge = bridge;
        _ui = ui;
        _centerClient = centerClient;
        _importer = importer;
        _settingsStore = settingsStore;
        _outbox = outbox;
        _dialogs = dialogs;
        _siteIdProvider = siteIdProvider;
        _logSettingsStore = logSettingsStore;
        _forwardMqttToggle = forwardMqttToggle;
        _configuration = configuration;

        MqttBroker = $"{mqtt.Host}:{mqtt.Port}" + (mqtt.UseTls ? " (TLS)" : "");
        MqttClientId = mqtt.ClientId ?? "—";
        DatabasePath = configuration["Persistence:ConnectionString"] ?? "";
        // ADR-027 P3-3：按 Name 定位 File sink，避免 WriteTo 增删后索引错位
        LogDirectory = Path.GetDirectoryName(configuration[DesktopPathConfig.FileSinkPathKey(configuration)]) ?? "";
        CollectionInterval = configuration["Collection:IntervalMs"] ?? "";
        ForwarderInterval = configuration["Forwarder:IntervalMs"] ?? "";

        // ADR-033 阶段 2：回填上次保存的中心地址/Token
        var saved = _settingsStore.Load();
        CenterUrl = saved.CenterUrl;
        CenterToken = saved.CenterToken;

        SiteId = siteIdProvider.Current;
        ForwardMqttEnabled = _forwardMqttToggle.IsEnabled;

        _bridge.FrameReady += OnFrame;
    }

    private bool CanToggleForwardMqtt => !IsSavingForwardMqtt;

    /// <summary>
    /// 切换 MQTT 上云转发开关（ADR-059）：立即持久化（desktop-settings.json），
    /// 成功更新状态提示；失败回滚开关到持久值并提示。关闭仅暂停 MQTT 上云，采集/本地存储/告警不受影响。
    /// </summary>
    /// <param name="enabled">目标状态（ToggleButton 的 IsChecked 透传）</param>
    [RelayCommand(CanExecute = nameof(CanToggleForwardMqtt))]
    private async Task ToggleForwardMqttAsync(bool enabled)
    {
        IsSavingForwardMqtt = true;
        ToggleForwardMqttCommand.NotifyCanExecuteChanged();
        try
        {
            ForwardMqttStatus = "正在保存…";
            var result = await _forwardMqttToggle.SetEnabledAsync(enabled);
            if (result.IsSuccess)
            {
                // 成功后显式同步 VM 状态（不依赖 UI 双向绑定已先翻转，命令被程序化调用时也自洽）
                ForwardMqttEnabled = enabled;
                ForwardMqttStatus = enabled
                    ? "已开启：采集/本地存储/告警不受影响，数据继续 MQTT 上云。"
                    : "已关闭：照常采集与本地存储，仅暂停 MQTT 上云；恢复后从关闭时刻续传。";
            }
            else
            {
                ForwardMqttStatus = $"保存失败：{result.Error!.Message}";
                ForwardMqttEnabled = _forwardMqttToggle.IsEnabled;
            }
        }
        finally
        {
            IsSavingForwardMqtt = false;
            ToggleForwardMqttCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>保存站点标识：校验后持久化；运行中采集/转发仍用旧值，重启后生效。</summary>
    [RelayCommand]
    private void SaveSiteId()
    {
        var result = _siteIdProvider.Save(SiteId);
        SiteIdStatus = result.IsSuccess
            ? $"已保存：{_siteIdProvider.Current}（重启后生效）"
            : $"保存失败：{result.Error!.Message}";
        SiteId = _siteIdProvider.Current;
    }

    /// <summary>重新生成站点标识（随机生成，概率唯一；中心 sites 唯一索引兜底）。</summary>
    [RelayCommand]
    private void RegenerateSiteId()
    {
        SiteId = _siteIdProvider.Regenerate();
        SiteIdStatus = $"已重新生成：{SiteId}（重启后生效）";
    }

    /// <summary>
    /// 保存自定义日志目录：校验绝对路径并预创建，持久化到 desktop-settings.json；
    /// 重启后由 DesktopPathConfig.Apply 生效（环境变量仍优先）。空值恢复默认目录。
    /// </summary>
    [RelayCommand]
    private void SaveLogDirectory()
    {
        var directory = LogDirectory?.Trim() ?? "";
        if (directory.Length == 0)
        {
        var cleared = _logSettingsStore.Load();
        cleared.LogDirectory = "";
        _logSettingsStore.Save(cleared);
        LogDirectoryStatus = "已清除自定义日志目录，重启后恢复默认位置";
        return;
        }

        if (!Path.IsPathRooted(directory))
        {
            LogDirectoryStatus = "保存失败：日志目录必须是绝对路径（如 D:\\logs）";
            return;
        }

        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception ex)
        {
            LogDirectoryStatus = $"保存失败：无法创建目录（{ex.Message}）";
            return;
        }

        // ADR-059：加载→改字段→保存（保留 ForwarderMqttEnabled，避免与 MQTT 开关互相覆盖）
        var saved = _logSettingsStore.Load();
        saved.LogDirectory = directory;
        _logSettingsStore.Save(saved);
        LogDirectoryStatus = $"已保存：{directory}（重启后生效）";
    }

    private bool CanImport => !IsImporting && !string.IsNullOrWhiteSpace(CenterUrl);

    /// <summary>
    /// 「从中心导入」：拉取中心快照 → 确认覆盖 → 以中心为准重置本地。
    /// 地址/Token 在发起时即持久化（本机会话内保留，便于失败后重试）。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanImport))]
    private async Task ImportFromCenterAsync()
    {
        var centerUrl = CenterUrl.Trim();
        var token = CenterToken.Trim();

        IsImporting = true;
        ImportStatusText = "正在连接中心…";
        try
        {
            _settingsStore.Save(new CenterSyncSettings { CenterUrl = centerUrl, CenterToken = token });

            // ADR-035 方案 A：手动导入也按本站点过滤（中心导出只返回本站点设备）
            var siteId = SiteOptions.Resolve(_configuration["Site:Id"]);
            var snapshotResult = await _centerClient.FetchSnapshotAsync(centerUrl, token, siteId);
            if (snapshotResult.IsFailure)
            {
                ImportStatusText = $"从中心导入失败：{snapshotResult.Error!.Message}";
                return;
            }

            var snapshot = snapshotResult.Value!;
            var deviceCount = snapshot.Count;
            var pointCount = snapshot.Sum(d => d.Points.Count);

            // ADR-033：覆盖前提示会覆盖本地未上报改动（用户确认）
            if (!_dialogs.Confirm("从中心导入",
                $"将以中心配置重置本地：导入 {deviceCount} 台设备、{pointCount} 个点位。\n" +
                "本地现有配置将被覆盖，未上报改动无法恢复。是否继续？"))
            {
                ImportStatusText = "已取消导入";
                return;
            }

            var importResult = await _importer.ImportAsync(snapshot);
            if (importResult.IsFailure)
            {
                ImportStatusText = $"从中心导入失败：{importResult.Error!.Message}";
                return;
            }

            var summary = importResult.Value!;
            // ADR-033 阶段 3/4：手动导入=以中心为准重置本地，本地与中心一致，清空待上报队列
            await _outbox.ClearAllAsync();
            ImportStatusText =
                $"导入完成：{summary.ImportedDevices} 台设备、{summary.ImportedPoints} 个点位；" +
                $"移除本地 {summary.RemovedDevices} 台设备、{summary.RemovedPoints} 个点位";
        }
        catch (Exception ex)
        {
            // 兜底：设置写入/解析等意外异常不崩 UI，给出可读提示（ADR-029 错误路径有提示）
            ImportStatusText = $"从中心导入失败：{ex.Message}";
        }
        finally
        {
            IsImporting = false;
        }
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



