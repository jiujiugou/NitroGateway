namespace NitroGateway.Security.Auth;

/// <summary>Token 签发结果状态（供 AuthController 区分响应，避免向攻击者泄露账号是否存在）</summary>
public enum TokenIssueStatus
{
    /// <summary>签发成功</summary>
    Success,

    /// <summary>用户不存在（与密码错误同返回 401，不泄露账号存在性）</summary>
    UserNotFound,

    /// <summary>密码错误（与用户不存在同返回 401）</summary>
    InvalidPassword,

    /// <summary>账号已停用（返回 403，便于管理方感知启停状态）</summary>
    Disabled
}

/// <summary>
/// Token 签发结果。<see cref="Token"/> 仅在 <see cref="TokenIssueStatus.Success"/> 时非空。
/// </summary>
public sealed record TokenIssueResult(TokenIssueStatus Status, string? Token = null)
{
    /// <summary>是否签发成功</summary>
    public bool IsSuccess => Status == TokenIssueStatus.Success;

    /// <summary>成功结果</summary>
    public static TokenIssueResult Ok(string token) => new(TokenIssueStatus.Success, token);
}
