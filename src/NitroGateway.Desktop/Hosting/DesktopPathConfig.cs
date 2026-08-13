using System.IO;
using Microsoft.Extensions.Configuration;
using NitroGateway.Desktop.Services;

namespace NitroGateway.Desktop.Hosting;

/// <summary>
/// ADR-026 D4：桌面端路径默认值。SQLite 库与日志缺省落到
/// <c>%LocalAppData%\NitroGateway</c>（<see cref="Environment.SpecialFolder.LocalApplicationData"/>），
    /// 环境变量（<c>Persistence__ConnectionString</c> / <c>Serilog__WriteTo__N__Args__path</c>）可覆盖。
/// </summary>
internal static class DesktopPathConfig
{
    /// <summary>默认数据目录：%LocalAppData%\NitroGateway</summary>
    public static string DefaultDataDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NitroGateway");

    /// <summary>
    /// 应用路径默认值。
    /// </summary>
    /// <param name="configuration">宿主配置（ConfigurationManager，索引写入立即生效）</param>
    /// <param name="dataDirectory">数据目录；测试可注入临时目录，缺省用 <c>%LocalAppData%\NitroGateway</c></param>
    /// <param name="settingsStore">桌面端本地设置；设置页自定义日志目录在此生效（环境变量仍优先）</param>
    public static void Apply(ConfigurationManager configuration, string? dataDirectory = null,
        IDesktopSettingsStore? settingsStore = null)
    {
        var connectionString = configuration["Persistence:ConnectionString"];
        var logPathKey = FileSinkPathKey(configuration);
        var logPathEnv = ReadLogPathEnv(logPathKey);

        var needsDataDirectory =
            string.IsNullOrWhiteSpace(connectionString) ||
            string.IsNullOrWhiteSpace(logPathEnv);
        if (!needsDataDirectory)
            return;

        var dataDir = dataDirectory ?? DefaultDataDirectory();
        Directory.CreateDirectory(dataDir);

        if (string.IsNullOrWhiteSpace(connectionString))
            configuration["Persistence:ConnectionString"] = $"Data Source={Path.Combine(dataDir, "nitrogateway.db")}";

        // appsettings 中的相对路径仅为占位；日志目录优先级：环境变量 > 设置页自定义（desktop-settings.json）> LocalAppData\logs
        if (string.IsNullOrWhiteSpace(logPathEnv))
        {
            var customLogDir = settingsStore?.Load().LogDirectory?.Trim();
            configuration[logPathKey] =
                !string.IsNullOrWhiteSpace(customLogDir) && TryPrepareLogDirectory(customLogDir)
                    ? Path.Combine(customLogDir, "nitrogateway-desktop-.log")
                    : Path.Combine(dataDir, "logs", "nitrogateway-desktop-.log");
        }
    }

    /// <summary>
    /// 校验并准备自定义日志目录：仅接受绝对路径且能创建；
    /// 失败（相对路径/无权限/路径非法）时回退默认目录，避免损坏的本地设置阻断启动。
    /// </summary>
    private static bool TryPrepareLogDirectory(string path)
    {
        try
        {
            if (!Path.IsPathRooted(path))
                return false;
            Directory.CreateDirectory(path);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 定位 Serilog File sink 的配置键（<c>Serilog:WriteTo:N:Args:path</c>）。
    /// 按 Name 匹配而非硬编码数组索引（ADR-027 P3-3），WriteTo 增删项不会错位；
    /// 未配置 File sink 时回退索引 0（新增 WriteTo 数组时从首项开始）。
    /// </summary>
    public static string FileSinkPathKey(IConfiguration configuration)
    {
        var file = configuration.GetSection("Serilog:WriteTo").GetChildren()
            .FirstOrDefault(s => string.Equals(s["Name"], "File", StringComparison.OrdinalIgnoreCase));
        return file is not null && int.TryParse(file.Key, out var index)
            ? $"Serilog:WriteTo:{index}:Args:path"
            : "Serilog:WriteTo:0:Args:path";
    }

    /// <summary>
    /// 读取日志路径环境变量：优先 File sink 当前索引对应的
    /// <c>Serilog__WriteTo__N__Args__path</c>；同时兼容早期文档化的索引 1
    /// （ADR-027 P3-5 移除 Console 后 File 索引从 1 变为 0）。
    /// </summary>
    private static string? ReadLogPathEnv(string logPathKey)
    {
        var currentIndex = logPathKey.Split(':').ElementAtOrDefault(2) is { } key &&
            int.TryParse(key, out var index)
            ? index
            : (int?)null;

        if (currentIndex is int i)
        {
            var value = Environment.GetEnvironmentVariable($"Serilog__WriteTo__{i}__Args__path");
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        if (currentIndex != 1)
        {
            var value = Environment.GetEnvironmentVariable("Serilog__WriteTo__1__Args__path");
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }
        return null;
    }
}
