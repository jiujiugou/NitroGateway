using System.IO;
using System.Text.Json;
using NitroGateway.Desktop.Hosting;

namespace NitroGateway.Desktop.Services.Sync;

/// <summary>站点标识本地设置（ADR-036）。</summary>
public sealed class SiteSettings
{
    /// <summary>当前站点标识；空串表示尚未初始化</summary>
    public string SiteId { get; set; } = "";
}

/// <summary>站点标识本地存储接口（可测试替身）。</summary>
public interface ISiteSettingsStore
{
    SiteSettings Load();
    void Save(SiteSettings settings);
}

/// <summary>
/// siteId 本地存储（ADR-036）：<c>%LocalAppData%\NitroGateway\site.json</c>，
/// 与 center-sync.json 同目录同模式；文件损坏/缺失回退空值，不阻断启动。
/// </summary>
public sealed class SiteSettingsStore : ISiteSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _filePath;

    /// <param name="filePath">设置文件路径；缺省为 %LocalAppData%\NitroGateway\site.json</param>
    public SiteSettingsStore(string? filePath = null)
        => _filePath = filePath ?? Path.Combine(DesktopPathConfig.DefaultDataDirectory(), "site.json");

    public SiteSettings Load()
    {
        try
        {
            if (!File.Exists(_filePath))
                return new SiteSettings();
            return JsonSerializer.Deserialize<SiteSettings>(File.ReadAllText(_filePath), JsonOptions)
                ?? new SiteSettings();
        }
        catch (Exception)
        {
            // 设置文件损坏不应阻断启动：回退空值（下次解析自动生成新 siteId）
            return new SiteSettings();
        }
    }

    public void Save(SiteSettings settings)
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(_filePath, JsonSerializer.Serialize(settings, JsonOptions));
    }
}