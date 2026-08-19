using Xunit;
using System.IO;
using Microsoft.Extensions.Configuration;
using NitroGateway.Desktop.Hosting;
using NitroGateway.Desktop.Services.Settings;


namespace NitroGateway.UnitTests;

/// <summary>
/// ADR-026 D4 + ADR-027 P3-3：桌面路径默认值单测
/// （%LocalAppData% 缺省 + 环境变量覆盖 + File sink 按 Name 定位而非硬编码索引）。
/// </summary>
public sealed class DesktopPathConfigTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(), "NitroGateway.Desktop.Tests", Guid.NewGuid().ToString("N"));

    private readonly string? _originalLogEnv0 =
        Environment.GetEnvironmentVariable("Serilog__WriteTo__0__Args__path");
    private readonly string? _originalLogEnv1 =
        Environment.GetEnvironmentVariable("Serilog__WriteTo__1__Args__path");

    [Fact]
    public void Apply_sets_default_connection_string_when_missing()
    {
        var config = new ConfigurationManager();
        DesktopPathConfig.Apply(config, _tempDir);

        Assert.Equal($"Data Source={Path.Combine(_tempDir, "nitrogateway.db")}",
            config["Persistence:ConnectionString"]);
    }

    [Fact]
    public void Apply_keeps_explicit_connection_string()
    {
        var config = new ConfigurationManager();
        config["Persistence:ConnectionString"] = "Data Source=explicit.db";

        DesktopPathConfig.Apply(config, _tempDir);

        Assert.Equal("Data Source=explicit.db", config["Persistence:ConnectionString"]);
    }

    [Fact]
    public void Apply_sets_log_path_when_env_not_set()
    {
        Environment.SetEnvironmentVariable("Serilog__WriteTo__0__Args__path", null);
        Environment.SetEnvironmentVariable("Serilog__WriteTo__1__Args__path", null);
        var config = new ConfigurationManager();

        DesktopPathConfig.Apply(config, _tempDir);

        // ADR-027 P3-5 移除 Console 后 File sink 位于索引 0；无显式配置时按默认索引 0 写入
        Assert.Equal(Path.Combine(_tempDir, "logs", "nitrogateway-desktop-.log"),
            config["Serilog:WriteTo:0:Args:path"]);
    }

    [Fact]
    public void Apply_writes_log_path_at_configured_file_sink_index()
    {
        Environment.SetEnvironmentVariable("Serilog__WriteTo__0__Args__path", null);
        var config = new ConfigurationManager();
        config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Serilog:WriteTo:0:Name"] = "File",
            ["Serilog:WriteTo:0:Args:path"] = "logs/placeholder.log",
            ["Serilog:WriteTo:1:Name"] = "Debug"
        });

        DesktopPathConfig.Apply(config, _tempDir);

        // 按 Name 找到 File sink（索引 0），Debug sink（索引 1）不受影响
        Assert.Equal(Path.Combine(_tempDir, "logs", "nitrogateway-desktop-.log"),
            config["Serilog:WriteTo:0:Args:path"]);
        Assert.Null(config["Serilog:WriteTo:1:Args:path"]);
    }

    [Fact]
    public void Apply_keeps_log_path_when_env_set()
    {
        Environment.SetEnvironmentVariable("Serilog__WriteTo__1__Args__path", @"C:\custom\log-.log");
        var config = new ConfigurationManager();
        config["Serilog:WriteTo:1:Args:path"] = @"C:\appsettings\placeholder.log";

        DesktopPathConfig.Apply(config, _tempDir);

        // 环境变量已显式指定时，Apply 不得覆盖配置中的路径
        Assert.Equal(@"C:\appsettings\placeholder.log", config["Serilog:WriteTo:1:Args:path"]);
    }

    [Fact]
    public void Apply_keeps_log_path_when_env_set_at_file_sink_index()
    {
        Environment.SetEnvironmentVariable("Serilog__WriteTo__0__Args__path", @"C:\custom\log-.log");
        var config = new ConfigurationManager();
        config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Serilog:WriteTo:0:Name"] = "File",
            ["Serilog:WriteTo:0:Args:path"] = "logs/placeholder.log"
        });

        DesktopPathConfig.Apply(config, _tempDir);

        Assert.Equal("logs/placeholder.log", config["Serilog:WriteTo:0:Args:path"]);
    }

    [Fact]
    public void Apply_uses_persisted_log_directory_when_env_not_set()
    {
        Environment.SetEnvironmentVariable("Serilog__WriteTo__0__Args__path", null);
        Environment.SetEnvironmentVariable("Serilog__WriteTo__1__Args__path", null);
        var config = new ConfigurationManager();
        var store = new DesktopSettingsStore(Path.Combine(_tempDir, "desktop-settings.json"));
        var customDir = Path.Combine(_tempDir, "custom-logs");
        store.Save(new DesktopSettings { LogDirectory = customDir });

        DesktopPathConfig.Apply(config, _tempDir, store);

        Assert.Equal(Path.Combine(customDir, "nitrogateway-desktop-.log"),
            config["Serilog:WriteTo:0:Args:path"]);
    }

    [Fact]
    public void Apply_falls_back_to_default_when_persisted_log_directory_invalid()
    {
        Environment.SetEnvironmentVariable("Serilog__WriteTo__0__Args__path", null);
        Environment.SetEnvironmentVariable("Serilog__WriteTo__1__Args__path", null);
        var config = new ConfigurationManager();
        var store = new DesktopSettingsStore(Path.Combine(_tempDir, "desktop-settings.json"));
        store.Save(new DesktopSettings { LogDirectory = "relative\\logs" });

        DesktopPathConfig.Apply(config, _tempDir, store);

        Assert.Equal(Path.Combine(_tempDir, "logs", "nitrogateway-desktop-.log"),
            config["Serilog:WriteTo:0:Args:path"]);
    }

    [Fact]
    public void Apply_ignores_persisted_log_directory_when_env_set()
    {
        Environment.SetEnvironmentVariable("Serilog__WriteTo__0__Args__path", @"C:\custom\log-.log");
        var config = new ConfigurationManager();
        config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Serilog:WriteTo:0:Name"] = "File",
            ["Serilog:WriteTo:0:Args:path"] = "logs/placeholder.log"
        });
        var store = new DesktopSettingsStore(Path.Combine(_tempDir, "desktop-settings.json"));
        store.Save(new DesktopSettings { LogDirectory = Path.Combine(_tempDir, "custom-logs") });

        DesktopPathConfig.Apply(config, _tempDir, store);

        Assert.Equal("logs/placeholder.log", config["Serilog:WriteTo:0:Args:path"]);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("Serilog__WriteTo__0__Args__path", _originalLogEnv0);
        Environment.SetEnvironmentVariable("Serilog__WriteTo__1__Args__path", _originalLogEnv1);
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* 清理失败可忽略 */ }
    }
}
