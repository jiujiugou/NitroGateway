using Microsoft.Extensions.Configuration;

namespace NitroGateway.Webapi.Deployment;

/// <summary>
/// 部署形态（ADR-035 第 0 步）：决定 Webapi 是否注册采集/转发/MQTT 发布模块。
/// 同一镜像按配置切换，避免维护两份产物。
/// </summary>
public enum DeploymentMode
{
    /// <summary>
    /// 边缘网关（默认）：注册采集引擎、上行转发与 MQTT 发布，兼容现有单现场/一体机部署。
    /// </summary>
    Gateway,

    /// <summary>
    /// 平台中心：不注册采集/转发/MQTT 发布（中心库数据写点唯一为 Ingest），
    /// 仅保留管理 API、Web 展示与告警规则/告警查询（仓储由 AddNitroSqlite 提供）。
    /// </summary>
    Center
}

/// <summary>
/// <c>Deployment:Mode</c> 配置解析。缺省按 <see cref="DeploymentMode.Gateway"/> 处理；
/// 未知值启动即抛错——防止中心漏配或拼写错误导致误跑采集/转发（ADR-034 根因）。
/// </summary>
public static class DeploymentModeParser
{
    /// <summary>配置键：Deployment:Mode</summary>
    public const string ModeKey = "Deployment:Mode";

    /// <summary>解析部署形态；配置缺失或为空时按 Gateway 处理。</summary>
    public static DeploymentMode Parse(IConfiguration configuration)
    {
        var raw = configuration[ModeKey]?.Trim();
        if (string.IsNullOrEmpty(raw))
            return DeploymentMode.Gateway;

        if (Enum.TryParse<DeploymentMode>(raw, ignoreCase: true, out var mode))
            return mode;

        throw new InvalidOperationException(
            $"Deployment:Mode 取值必须为 Gateway 或 Center，实际为: {raw}（Center 形态禁止误跑采集/转发）");
    }
}
