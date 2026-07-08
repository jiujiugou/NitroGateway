using System.Diagnostics;

namespace NitroGateway.Telemetry.Tracing;

/// <summary>
/// 全局 ActivitySource。业务代码通过本类的 <see cref="Source"/> 创建 Activity Span，
/// 禁止在各模块中自行 new ActivitySource。
/// </summary>
public static class GatewayActivitySource
{
    /// <summary>NitroGateway 全局 ActivitySource。名称固定为 "NitroGateway"，版本 1.0</summary>
    public static readonly ActivitySource Source = new("NitroGateway", "1.0.0");
}
