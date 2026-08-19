using System.IO;
using System.Text.Json;
using NitroGateway.Desktop.Hosting;

namespace NitroGateway.Desktop.Services.Settings;

/// <summary>桌面端本地设置：日志目录等（设置页可自行配置）。</summary>
public sealed class DesktopSettings
{
    /// <summary>
    /// 自定义日志目录；空字符串表示使用默认位置
    /// （%LocalAppData%\NitroGateway\logs，ADR-026 D4）。
    /// </summary>
    public string LogDirectory { get; set; } = "";
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
            return JsonSerializer.Deserialize<DesktopSettings>(File.ReadAllText(_filePath), JsonOptions)
                ?? new DesktopSettings();
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
        File.WriteAllText(_filePath, JsonSerializer.Serialize(settings, JsonOptions));
    }
}
