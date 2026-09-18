using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using NitroGateway.Desktop.Hosting;
using NitroGateway.Desktop.Services.Settings;

namespace NitroGateway.Desktop.Services.Sync;

public sealed class CenterSyncSettings
{
    /// <summary>中心 Webapi 基地址，如 "http://center.example.com:5100"</summary>
    public string CenterUrl { get; set; } = "";

    /// <summary>中心 JWT Token（调用导出接口的 Bearer 凭证）；仅内存，序列化时忽略。</summary>
    [JsonIgnore]
    public string CenterToken { get; set; } = "";

    /// <summary>DPAPI 加密后的 Token（CurrentUser 作用域，Base64），落盘字段。</summary>
    public string CenterTokenEncrypted { get; set; } = "";
}

/// <summary>中心同步设置存取（可测试替身）。</summary>
public interface ICenterSyncSettingsStore
{
    CenterSyncSettings Load();
    void Save(CenterSyncSettings settings);
}

public sealed class CenterSyncSettingsStore : ICenterSyncSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _filePath;

    /// <param name="filePath">设置文件路径；缺省为 %LocalAppData%\NitroGateway\center-sync.json</param>
    public CenterSyncSettingsStore(string? filePath = null)
        => _filePath = filePath ?? Path.Combine(DesktopPathConfig.DefaultDataDirectory(), "center-sync.json");

    public CenterSyncSettings Load()
    {
        try
        {
            if (!File.Exists(_filePath))
                return new CenterSyncSettings();

            var raw = File.ReadAllText(_filePath);
            var settings = JsonSerializer.Deserialize<CenterSyncSettings>(raw, JsonOptions)
                ?? new CenterSyncSettings();

            if (!string.IsNullOrEmpty(settings.CenterTokenEncrypted))
            {
                // 密文损坏/跨用户解密失败时按空 Token 处理，不阻断设置页
                try
                {
                    settings.CenterToken = DpapiProtector.Unprotect(settings.CenterTokenEncrypted);
                }
                catch (Exception)
                {
                    settings.CenterToken = "";
                }
                return settings;
            }

            var legacy = JsonSerializer.Deserialize<LegacyCenterSyncSettings>(raw, JsonOptions);
            if (legacy is { CenterToken.Length: > 0 })
            {
                settings.CenterToken = legacy.CenterToken;
                try { Save(settings); } catch { /* 迁移改写失败不阻断读取 */ }
            }
            return settings;
        }
        catch (Exception)
        {
            // 设置文件损坏不应阻断设置页：回退空设置（下次保存会覆盖）
            return new CenterSyncSettings();
        }
    }

    public void Save(CenterSyncSettings settings)
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        // 只落盘加密 Token；明文 CenterToken 由 [JsonIgnore] 排除在序列化之外
        var persisted = new CenterSyncSettings
        {
            CenterUrl = settings.CenterUrl,
            CenterTokenEncrypted = string.IsNullOrEmpty(settings.CenterToken)
                ? ""
                : DpapiProtector.Protect(settings.CenterToken)
        };
        File.WriteAllText(_filePath, JsonSerializer.Serialize(persisted, JsonOptions));
    }

    /// <summary>旧版明文设置文件形状（仅用于迁移读取）。</summary>
    private sealed class LegacyCenterSyncSettings
    {
        public string CenterUrl { get; set; } = "";
        public string CenterToken { get; set; } = "";
    }
}
