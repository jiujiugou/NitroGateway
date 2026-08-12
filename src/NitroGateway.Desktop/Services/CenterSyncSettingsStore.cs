using System.IO;
using System.Text.Json;
using NitroGateway.Desktop.Hosting;

namespace NitroGateway.Desktop.Services;

/// <summary>
/// 中心同步设置（ADR-033 阶段 2）：中心地址与 Token。
/// 明文存于本机 <c>%LocalAppData%\NitroGateway\center-sync.json</c>（ADR-029 P5 方向），
/// 仅本机用户可读写；如需更强保护后续可换 DPAPI，v1 与 SQLite 同目录同权限即可。
/// </summary>
public sealed class CenterSyncSettings
{
    /// <summary>中心 Webapi 基地址，如 "http://center.example.com:5100"</summary>
    public string CenterUrl { get; set; } = "";

    /// <summary>中心 JWT Token（调用导出接口的 Bearer 凭证）</summary>
    public string CenterToken { get; set; } = "";
}

/// <summary>中心同步设置存取（可测试替身）。</summary>
public interface ICenterSyncSettingsStore
{
    CenterSyncSettings Load();
    void Save(CenterSyncSettings settings);
}

/// <summary>
/// 中心同步设置的 JSON 文件实现。文件损坏/缺失时回退默认空设置，不阻断设置页。
/// </summary>
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
            return JsonSerializer.Deserialize<CenterSyncSettings>(File.ReadAllText(_filePath), JsonOptions)
                ?? new CenterSyncSettings();
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
        File.WriteAllText(_filePath, JsonSerializer.Serialize(settings, JsonOptions));
    }
}
