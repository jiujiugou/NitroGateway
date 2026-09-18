using Microsoft.Extensions.Configuration;
using NitroGateway.Desktop.Services.Settings;

namespace NitroGateway.Desktop.Hosting;

internal static class MqttDesktopConfig
{
    /// <summary>
    /// 将设置页保存的 MQTT 连接参数写回宿主配置；须在 <c>AddNitroMqtt</c> 绑定 Options 之前调用。
    /// 未保存过（<see cref="DesktopSettings.MqttHost"/> 为空）或对应环境变量已设置时跳过，保留原默认。
    /// </summary>
    /// <param name="configuration">宿主配置（ConfigurationManager，索引写入立即生效）</param>
    /// <param name="settingsStore">桌面本地设置；测试可注入临时存储，缺省读默认文件</param>
    public static void Apply(ConfigurationManager configuration, IDesktopSettingsStore? settingsStore = null)
    {
        var settings = (settingsStore ?? new DesktopSettingsStore()).Load();
        if (string.IsNullOrWhiteSpace(settings.MqttHost))
            return; // 未保存过 MQTT 连接参数：走 appsettings 默认 / 环境变量

        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MQTT__Host")))
            configuration["MQTT:Host"] = settings.MqttHost;

        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MQTT__Port")))
            configuration["MQTT:Port"] = settings.MqttPort.ToString(System.Globalization.CultureInfo.InvariantCulture);

        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MQTT__UseTls")))
            configuration["MQTT:UseTls"] = settings.MqttUseTls ? "true" : "false";

        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MQTT__Username")))
            configuration["MQTT:Username"] = settings.MqttUsername;

        // 仅保存过密码才覆盖（区分「未配置」与「明确空密码匿名」）；环境变量仍优先
        if (settings.MqttPasswordConfigured &&
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MQTT__Password")))
            configuration["MQTT:Password"] = settings.MqttPassword;
    }
}
