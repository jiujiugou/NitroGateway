namespace NitroGateway.Security.Auth;

public sealed class UserAccount
{
    /// <summary>主键（users.id，自增）</summary>
    public int Id { get; init; }

    /// <summary>用户名（唯一，大小写敏感；登录前由调用方 Trim）</summary>
    public required string Username { get; init; }

    /// <summary>密码哈希（PasswordHasher 生成，勿外泄）</summary>
    public required string PasswordHash { get; set; }

    /// <summary>角色：Admin / Operator / Viewer（<see cref="NitroGateway.Security.Roles"/>）</summary>
    public required string Role { get; set; }

    /// <summary>是否启用；停用后登录被拒（403），不影响已签发 Token 失效前的使用</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>创建时间（UTC，O 格式字符串落库）</summary>
    public DateTime CreatedAt { get; init; }

    /// <summary>最近更新时间（UTC；角色/启停/密码变更时刷新）</summary>
    public DateTime UpdatedAt { get; set; }

    /// <summary>最近登录时间（UTC，null = 从未登录）</summary>
    public DateTime? LastLoginAt { get; set; }
}
