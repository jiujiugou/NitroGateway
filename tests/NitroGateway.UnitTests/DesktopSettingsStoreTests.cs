using System.IO;
using NitroGateway.Desktop.Services;
using Xunit;

namespace NitroGateway.UnitTests;

/// <summary>桌面端本地设置存储（desktop-settings.json）读写与容错。</summary>
public sealed class DesktopSettingsStoreTests : IDisposable
{
    private readonly string _file = Path.Combine(
        Path.GetTempPath(), "nitrogateway-tests", $"{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        try { File.Delete(_file); } catch { /* 清理失败可忽略 */ }
    }

    [Fact]
    public void Save_then_Load_roundtrips_log_directory()
    {
        var store = new DesktopSettingsStore(_file);

        store.Save(new DesktopSettings { LogDirectory = @"D:\logs\site1" });

        Assert.Equal(@"D:\logs\site1", new DesktopSettingsStore(_file).Load().LogDirectory);
    }

    [Fact]
    public void Load_missing_file_returns_empty_settings()
    {
        var settings = new DesktopSettingsStore(_file).Load();

        Assert.Equal("", settings.LogDirectory);
    }

    [Fact]
    public void Load_corrupted_file_returns_empty_settings()
    {
        var dir = Path.GetDirectoryName(_file)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(_file, "{ not json !!!");

        var settings = new DesktopSettingsStore(_file).Load();

        Assert.Equal("", settings.LogDirectory);
    }
}
