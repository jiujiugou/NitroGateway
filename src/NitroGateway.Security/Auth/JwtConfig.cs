namespace NitroGateway.Security.Auth;

/// <summary>JWT 签发配置</summary>
public sealed class JwtConfig
{
    /// <summary>签发密钥（至少 32 字符）。生产环境从环境变量读取</summary>
    public required string SecretKey { get; init; }

    /// <summary>Token 有效期（小时），默认 8</summary>
    public int ExpireHours { get; init; } = 8;

    /// <summary>签发者</summary>
    public string Issuer { get; init; } = "NitroGateway";

    /// <summary>受众</summary>
    public string Audience { get; init; } = "NitroGateway";
}
