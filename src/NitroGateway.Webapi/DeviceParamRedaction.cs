namespace NitroGateway.Webapi;

/// <summary>
/// OPC UA 连接参数中敏感键的对外收口（ADR-073 D5）。
/// 域内/驱动路径的 Device.Connection.Parameters 在仓储读取时已解密为明文；凡离开边缘的序列化边界
/// （API 响应 / 中心同步上报）一律剔除 <c>Password</c> 明文，只暴露 <c>hasPassword</c> 标志，
/// 保证明文密码不落 SQLite、不出 API、不出 outbox/中心同步负载（载荷墙硬约束）。
/// </summary>
internal static class DeviceParamRedaction
{
    /// <summary>OPC UA 连接参数中的密码键（PascalCase，与设备参数字典约定一致）</summary>
    internal const string PasswordKey = "Password";

    /// <summary>参数是否含非空密码（用于对外暴露 hasPassword 标志）。</summary>
    internal static bool HasPassword(IReadOnlyDictionary<string, object> parameters)
        => parameters.TryGetValue(PasswordKey, out var value) && value is string s && !string.IsNullOrEmpty(s);

    /// <summary>
    /// 返回剔除 <c>Password</c> 后的参数副本（对外序列化用）；入参不含 Password 时也返回副本，
    /// 避免调用方与响应共享可变字典。Modbus/S7 无 Password 键，行为与原本一致。
    /// </summary>
    internal static Dictionary<string, object> WithoutPassword(IReadOnlyDictionary<string, object> parameters)
    {
        var copy = new Dictionary<string, object>(parameters, StringComparer.Ordinal);
        copy.Remove(PasswordKey);
        return copy;
    }
}
