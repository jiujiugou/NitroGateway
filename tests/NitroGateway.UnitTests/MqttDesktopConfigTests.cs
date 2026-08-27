using Microsoft.Extensions.Configuration;
using NitroGateway.Desktop.Hosting;
using NitroGateway.Desktop.Services.Settings;
using Xunit;

namespace NitroGateway.UnitTests;

/// <summary>
/// ADR-067：设置页保存的 MQTT 连接参数（desktop-settings.json）启动覆盖 appsettings；
/// 优先级：环境变量 ＞ 持久化设置 ＞ appsettings 默认。
/// </summary>
public sealed class MqttDesktopConfigTests
{
    [Fact]
    public void Apply_not_saved_leaves_config_unchanged()
    {
        var configuration = BuildConfig();
        var file = TempFile();
        try
        {
            MqttDesktopConfig.Apply(configuration, new DesktopSettingsStore(file));

            Assert.Equal("localhost", configuration["MQTT:Host"]);
            Assert.Equal("1883", configuration["MQTT:Port"]);
            Assert.Equal("default-user", configuration["MQTT:Username"]);
            Assert.Equal("default-pw", configuration["MQTT:Password"]);
        }
        finally
        {
            TryDelete(file);
        }
    }

    [Fact]
    public void Apply_saved_overrides_defaults()
    {
        var configuration = BuildConfig();
        var file = TempFile();
        try
        {
            new DesktopSettingsStore(file).Save(new DesktopSettings
            {
                MqttHost = "10.0.0.5",
                MqttPort = 8883,
                MqttUseTls = true,
                MqttUsername = "u",
                MqttPassword = "p",
                MqttPasswordConfigured = true
            });

            MqttDesktopConfig.Apply(configuration, new DesktopSettingsStore(file));

            Assert.Equal("10.0.0.5", configuration["MQTT:Host"]);
            Assert.Equal("8883", configuration["MQTT:Port"]);
            Assert.Equal("true", configuration["MQTT:UseTls"]);
            Assert.Equal("u", configuration["MQTT:Username"]);
            Assert.Equal("p", configuration["MQTT:Password"]);
        }
        finally
        {
            TryDelete(file);
        }
    }

    [Fact]
    public void Apply_empty_password_configured_still_overrides()
    {
        // 明确保存空密码（匿名）：覆盖 appsettings 默认密码
        var configuration = BuildConfig();
        var file = TempFile();
        try
        {
            new DesktopSettingsStore(file).Save(new DesktopSettings
            {
                MqttHost = "10.0.0.5",
                MqttPasswordConfigured = true,
                MqttPassword = ""
            });

            MqttDesktopConfig.Apply(configuration, new DesktopSettingsStore(file));

            Assert.Equal("", configuration["MQTT:Password"]);
        }
        finally
        {
            TryDelete(file);
        }
    }

    [Fact]
    public void Apply_environment_variable_wins_over_persisted()
    {
        var previous = Environment.GetEnvironmentVariable("MQTT__Host");
        var file = TempFile();
        try
        {
            Environment.SetEnvironmentVariable("MQTT__Host", "env.broker");
            var configuration = BuildConfig();
            // 模拟真实宿主：环境变量 provider 位于 appsettings 之上（更高优先级）
            configuration.AddEnvironmentVariables();
            new DesktopSettingsStore(file).Save(new DesktopSettings { MqttHost = "persisted.broker", MqttPort = 1883 });

            MqttDesktopConfig.Apply(configuration, new DesktopSettingsStore(file));

            // 环境变量存在：Apply 不写回持久值，读值仍来自环境变量
            Assert.Equal("env.broker", configuration["MQTT:Host"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MQTT__Host", previous);
            TryDelete(file);
        }
    }

    private static ConfigurationManager BuildConfig()
    {
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MQTT:Host"] = "localhost",
            ["MQTT:Port"] = "1883",
            ["MQTT:UseTls"] = "false",
            ["MQTT:Username"] = "default-user",
            ["MQTT:Password"] = "default-pw"
        });
        return configuration;
    }

    private static string TempFile() =>
        Path.Combine(Path.GetTempPath(), "nitrogateway-tests", $"mqtt-config-{Guid.NewGuid():N}.json");

    private static void TryDelete(string file)
    {
        try { File.Delete(file); } catch { /* 清理失败可忽略 */ }
    }
}
