namespace NitroGateway.Security.Auth;

/// <summary>JWT 签发配置</summary>
public sealed class JwtConfig
{
    public const string SectionName = "Security";

    /// <summary>JWT 密钥</summary>
    public string JwtSecretKey { get; init; } = string.Empty;

    /// <summary>签发者</summary>
    public string Issuer { get; init; } = "NitroGateway";

    /// <summary>接收者</summary>
    public string Audience { get; init; } = "NitroGateway";

    /// <summary>Token 有效期（小时）</summary>
    public int ExpireHours { get; init; } = 8;

    /// <summary>内置用户</summary>
    public List<UserConfig> Users { get; init; } = [];
}
