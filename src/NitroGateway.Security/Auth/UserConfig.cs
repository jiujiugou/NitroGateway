namespace NitroGateway.Security.Auth;

/// <summary>内置用户定义。工业网关用户量小，配置文件管理即可</summary>
public sealed class UserConfig
{
    /// <summary>用户名</summary>
    public required string Username { get; init; }

    /// <summary>密码哈希（由 PasswordHasher 生成，配置文件中存哈希，不支持明文）</summary>
    public required string Password { get; init; }

    /// <summary>角色：Admin / Operator / Viewer</summary>
    public string Role { get; init; } = "Operator";
}
