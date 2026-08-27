using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using NitroGateway.Desktop.Services.Settings;
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

    [Fact]
    public void ForwarderMqttEnabled_roundtrips_via_desktop_settings()
    {
        new DesktopSettingsStore(_file).Save(new DesktopSettings { ForwarderMqttEnabled = false });

        Assert.False(new DesktopSettingsStore(_file).Load().ForwarderMqttEnabled);
    }

    [Fact]
    public void DesktopSettings_default_ForwarderMqttEnabled_is_true()
    {
        // 缺省（未写入字段）视为启用
        Assert.True(new DesktopSettings().ForwarderMqttEnabled);
    }

    [Fact]
    public void Mqtt_fields_roundtrip_with_encrypted_password()
    {
        new DesktopSettingsStore(_file).Save(new DesktopSettings
        {
            MqttHost = "broker.local",
            MqttPort = 8883,
            MqttUseTls = true,
            MqttUsername = "user1",
            MqttPassword = "pw-secret",
            MqttPasswordConfigured = true
        });

        var loaded = new DesktopSettingsStore(_file).Load();

        Assert.Equal("broker.local", loaded.MqttHost);
        Assert.Equal(8883, loaded.MqttPort);
        Assert.True(loaded.MqttUseTls);
        Assert.Equal("user1", loaded.MqttUsername);
        Assert.Equal("pw-secret", loaded.MqttPassword);
        Assert.True(loaded.MqttPasswordConfigured);
        Assert.NotEqual("", loaded.MqttPasswordEncrypted);
    }

    [Fact]
    public void Mqtt_password_not_written_in_plaintext_to_disk()
    {
        new DesktopSettingsStore(_file).Save(new DesktopSettings
        {
            MqttHost = "broker.local",
            MqttPassword = "pw-secret",
            MqttPasswordConfigured = true
        });

        var raw = File.ReadAllText(_file);

        Assert.DoesNotContain("pw-secret", raw);
        // JsonSerializerDefaults.Web 使用 camelCase：落盘字段为 mqttPasswordEncrypted
        Assert.Contains("mqttPasswordEncrypted", raw);
    }

    [Fact]
    public void Mqtt_defaults_host_empty_and_port_1883()
    {
        // 未保存过 MQTT 连接参数：Host 为空 → 启动回退 appsettings/环境变量
        Assert.Equal("", new DesktopSettings().MqttHost);
        Assert.Equal(1883, new DesktopSettings().MqttPort);
        Assert.False(new DesktopSettings().MqttPasswordConfigured);
    }

    [Fact]
    public void DesktopToggle_Default_IsEnabled_true()
    {
        Assert.True(new DesktopForwardMqttToggle(
            new DesktopSettingsStore(_file), NullLogger<DesktopForwardMqttToggle>.Instance).IsEnabled);
    }

    [Fact]
    public async Task DesktopToggle_SetEnabled_persists_and_survives_restart()
    {
        var toggle = new DesktopForwardMqttToggle(
            new DesktopSettingsStore(_file), NullLogger<DesktopForwardMqttToggle>.Instance);

        var result = await toggle.SetEnabledAsync(false);

        Assert.True(result.IsSuccess);
        Assert.False(toggle.IsEnabled);
        // 已持久化到 desktop-settings.json：新实例加载后仍为关闭
        var restarted = new DesktopForwardMqttToggle(
            new DesktopSettingsStore(_file), NullLogger<DesktopForwardMqttToggle>.Instance);
        Assert.True((await restarted.InitializeAsync()).IsSuccess);
        Assert.False(restarted.IsEnabled);
    }

    [Fact]
    public async Task DesktopToggle_Initialize_missing_file_falls_back_to_enabled()
    {
        var toggle = new DesktopForwardMqttToggle(
            new DesktopSettingsStore(_file), NullLogger<DesktopForwardMqttToggle>.Instance);

        Assert.True((await toggle.InitializeAsync()).IsSuccess);
        Assert.True(toggle.IsEnabled);
    }
}
