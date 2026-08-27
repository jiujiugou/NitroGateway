using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using NitroGateway.Desktop.Hosting;

namespace NitroGateway.Desktop.Services.Settings;

/// <summary>
/// 桌面端本地设置：日志目录、MQTT 上云转发开关、MQTT Broker 连接参数（设置页可自行配置）。
/// 文件 <c>%LocalAppData%\NitroGateway\desktop-settings.json</c>。
/// </summary>
public sealed class DesktopSettings
{
    /// <summary>
    /// 自定义日志目录；空字符串表示使用默认位置
    /// （%LocalAppData%\NitroGateway\logs，ADR-026 D4）。
    /// </summary>
    public string LogDirectory { get; set; } = "";

    /// <summary>
    /// MQTT 上云转发开关（ADR-059）：false=仅暂停 MQTT 上云（采集/本地存储/告警不受影响），
    /// 缺省 true（启用）。由 <see cref="DesktopForwardMqttToggle"/> 读写，设置页开关即时生效、重启保持。
    /// </summary>
    public bool ForwarderMqttEnabled { get; set; } = true;

    /// <summary>
    /// MQTT Broker 地址（设置页可编辑，ADR-067）。空字符串 = 未保存过 MQTT 连接参数，
    /// 启动回退 appsettings 默认（localhost:1883）或环境变量（MQTT__Host 优先）。
    /// </summary>
    public string MqttHost { get; set; } = "";

    /// <summary>MQTT Broker 端口（1-65535，默认 1883）；仅 <see cref="MqttHost"/> 非空时生效。</summary>
    public int MqttPort { get; set; } = 1883;

    /// <summary>是否启用 TLS（端口通常 8883）。</summary>
    public bool MqttUseTls { get; set; }

    /// <summary>MQTT 用户名（可选）。</summary>
    public string MqttUsername { get; set; } = "";

    /// <summary>MQTT 密码明文；仅内存，序列化忽略（落盘走 DPAPI，ADR-037 S5 同模式）。</summary>
    [JsonIgnore]
    public string MqttPassword { get; set; } = "";

    /// <summary>DPAPI（CurrentUser）加密后的 MQTT 密码（Base64），落盘字段。</summary>
    public string MqttPasswordEncrypted { get; set; } = "";

    /// <summary>
    /// 是否保存过 MQTT 密码——区分「未配置」与「明确配置为空密码」（匿名登录）：
    /// 为 true 时启动用保存的密码（可能为空）覆盖 appsettings；为 false 时保持 appsettings 默认。
    /// </summary>
    public bool MqttPasswordConfigured { get; set; }
}

/// <summary>桌面端本地设置存储接口（可测试替身）。</summary>
public interface IDesktopSettingsStore
{
    DesktopSettings Load();
    void Save(DesktopSettings settings);
}

/// <summary>
/// 桌面端本地设置存储：<c>%LocalAppData%\NitroGateway\desktop-settings.json</c>，
/// 与 site.json / center-sync.json 同目录同模式；文件损坏或缺失去回退空值，不阻断启动。
/// </summary>
public sealed class DesktopSettingsStore : IDesktopSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _filePath;

    /// <param name="filePath">设置文件路径；缺省为 %LocalAppData%\NitroGateway\desktop-settings.json</param>
    public DesktopSettingsStore(string? filePath = null)
        => _filePath = filePath ?? Path.Combine(DesktopPathConfig.DefaultDataDirectory(), "desktop-settings.json");

    public DesktopSettings Load()
    {
        try
        {
            if (!File.Exists(_filePath))
                return new DesktopSettings();
            var settings = JsonSerializer.Deserialize<DesktopSettings>(File.ReadAllText(_filePath), JsonOptions)
                ?? new DesktopSettings();

            if (!string.IsNullOrEmpty(settings.MqttPasswordEncrypted))
            {
                // 密文损坏/跨用户解密失败时按空密码处理，不阻断设置页
                try
                {
                    settings.MqttPassword = DpapiProtector.Unprotect(settings.MqttPasswordEncrypted);
                }
                catch (Exception)
                {
                    settings.MqttPassword = "";
                }
            }
            return settings;
        }
        catch (Exception)
        {
            // 设置文件损坏不应阻断启动：回退空值（日志回到默认目录）。
            return new DesktopSettings();
        }
    }

    public void Save(DesktopSettings settings)
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        // 只落盘 DPAPI 加密后的密码；明文 MqttPassword 由 [JsonIgnore] 排除在序列化之外。
        var persisted = new DesktopSettings
        {
            LogDirectory = settings.LogDirectory,
            ForwarderMqttEnabled = settings.ForwarderMqttEnabled,
            MqttHost = settings.MqttHost,
            MqttPort = settings.MqttPort,
            MqttUseTls = settings.MqttUseTls,
            MqttUsername = settings.MqttUsername,
            MqttPasswordConfigured = settings.MqttPasswordConfigured,
            MqttPasswordEncrypted = string.IsNullOrEmpty(settings.MqttPassword)
                ? ""
                : DpapiProtector.Protect(settings.MqttPassword)
        };
        File.WriteAllText(_filePath, JsonSerializer.Serialize(persisted, JsonOptions));
    }
}
