using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using NitroGateway.Security;
using NitroGateway.Security.Auth;
using NitroGateway.Webapi.Models;

namespace NitroGateway.Webapi.Controllers;

/// <summary>
/// 用户管理 API（ADR-066：用户 DB 化，不走全量 Identity）。数据源为 SQLite users 表，
/// 新增/改角色/启停/重置密码即时生效（无需改配置重启）；密码只收哈希，接口不暴露。
/// <para><b>授权拆分说明：</b>AdminOnly 加在各管理动作上而非类级——「自助改密」（me/password）
/// 需对所有已登录角色开放，而 ASP.NET Core 类级 + 方法级 [Authorize] 是叠加关系（都要过），
/// 类级 AdminOnly 会连同自助改密一起锁死，故逐动作标注。</para>
/// </summary>
[ApiController, Route("api/[controller]")]
public class UserController : ControllerBase
{
    /// <summary>新建/重置/自助改密的密码最小长度（与配置测试账号 admin123 等长，防弱口令）</summary>
    public const int PasswordMinLength = 8;

    private readonly IUserStore _store;
    private readonly PasswordHasher<UserAccount> _hasher;

    /// <param name="store">用户存储（SQLite 实现，Dapper 单例）</param>
    /// <param name="hasher">密码哈希器（与登录校验共用同一实例）</param>
    public UserController(IUserStore store, PasswordHasher<UserAccount> hasher)
    {
        _store = store;
        _hasher = hasher;
    }

    // ═══════════ Admin 管理接口（仅 Admin） ═══════════

    /// <summary>用户列表（不含密码哈希，管理页展示；用户名排序）</summary>
    [HttpGet]
    [Authorize(Policy = "AdminOnly")]
    public async Task<ActionResult<ApiResponse<List<UserDto>>>> List(CancellationToken ct)
    {
        var users = await _store.ListAsync(ct);
        return Ok(ApiResponse<List<UserDto>>.Ok(users.Select(Map).ToList()));
    }

    /// <summary>新增用户（用户名唯一；新账号默认启用，角色必须 Admin/Operator/Viewer）</summary>
    [HttpPost]
    [Authorize(Policy = "AdminOnly")]
    public async Task<ActionResult<ApiResponse<UserDto>>> Create(
        [FromBody] CreateUserRequest req, CancellationToken ct)
    {
        var username = req.Username?.Trim() ?? "";
        var password = req.Password ?? "";
        var role = req.Role?.Trim() ?? "";

        if (username.Length is 0 or > 64)
            return BadRequest(ApiResponse<UserDto>.Fail("Users", "用户名不能为空且不超过 64 字符"));
        if (password.Length < PasswordMinLength)
            return BadRequest(ApiResponse<UserDto>.Fail("Users", $"密码长度不能少于 {PasswordMinLength} 位"));
        if (!IsValidRole(role))
            return BadRequest(ApiResponse<UserDto>.Fail("Users", "角色必须是 Admin / Operator / Viewer"));

        var hash = _hasher.HashPassword(new UserAccount { Username = username, PasswordHash = "", Role = role }, password);
        var result = await _store.CreateAsync(username, hash, role, ct);
        if (result.IsFailure)
            return Conflict(ApiResponse<UserDto>.Fail("Users", result.Error!.Message));

        return Ok(ApiResponse<UserDto>.Ok(Map(result.Value!)));
    }

    /// <summary>改角色（仅 Admin；不得把最后一个启用的 Admin 降级导致管理锁定）</summary>
    [HttpPut("{id:int}/role")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<ActionResult<ApiResponse<UserDto>>> ChangeRole(
        int id, [FromBody] ChangeRoleRequest req, CancellationToken ct)
    {
        var role = req.Role?.Trim() ?? "";
        if (!IsValidRole(role))
            return BadRequest(ApiResponse<UserDto>.Fail("Users", "角色必须是 Admin / Operator / Viewer"));

        var user = await _store.FindByIdAsync(id, ct);
        if (user is null)
            return NotFound(ApiResponse<UserDto>.Fail("Users", "用户不存在"));
        if (role != Roles.Admin && !await CanRemoveAdminAsync(user, ct))
            return BadRequest(ApiResponse<UserDto>.Fail("Users", "不能降级/停用最后一个启用的 Admin，避免管理锁定"));

        var ok = await _store.UpdateRoleAsync(id, role, ct);
        if (!ok)
            return NotFound(ApiResponse<UserDto>.Fail("Users", "用户不存在"));

        var updated = await _store.FindByIdAsync(id, ct);
        return Ok(ApiResponse<UserDto>.Ok(Map(updated!)));
    }

    /// <summary>启停用户（停用后下次登录被拒 403；最后一个启用 Admin 禁止停用）</summary>
    [HttpPut("{id:int}/enabled")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<ActionResult<ApiResponse<UserDto>>> SetEnabled(
        int id, [FromBody] SetEnabledRequest req, CancellationToken ct)
    {
        var user = await _store.FindByIdAsync(id, ct);
        if (user is null)
            return NotFound(ApiResponse<UserDto>.Fail("Users", "用户不存在"));
        if (!req.IsEnabled && user.IsEnabled && !await CanRemoveAdminAsync(user, ct))
            return BadRequest(ApiResponse<UserDto>.Fail("Users", "不能降级/停用最后一个启用的 Admin，避免管理锁定"));

        var ok = await _store.UpdateEnabledAsync(id, req.IsEnabled, ct);
        if (!ok)
            return NotFound(ApiResponse<UserDto>.Fail("Users", "用户不存在"));

        var updated = await _store.FindByIdAsync(id, ct);
        return Ok(ApiResponse<UserDto>.Ok(Map(updated!)));
    }

    /// <summary>重置密码（Admin 代改；写入新哈希，旧 Token 自然过期）</summary>
    [HttpPut("{id:int}/password")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<ActionResult<ApiResponse<UserDto>>> ResetPassword(
        int id, [FromBody] ResetPasswordRequest req, CancellationToken ct)
    {
        var newPassword = req.NewPassword ?? "";
        if (newPassword.Length < PasswordMinLength)
            return BadRequest(ApiResponse<UserDto>.Fail("Users", $"密码长度不能少于 {PasswordMinLength} 位"));

        var user = await _store.FindByIdAsync(id, ct);
        if (user is null)
            return NotFound(ApiResponse<UserDto>.Fail("Users", "用户不存在"));

        var hash = _hasher.HashPassword(user, newPassword);
        var ok = await _store.UpdatePasswordHashAsync(id, hash, ct);
        if (!ok)
            return NotFound(ApiResponse<UserDto>.Fail("Users", "用户不存在"));

        var updated = await _store.FindByIdAsync(id, ct);
        return Ok(ApiResponse<UserDto>.Ok(Map(updated!)));
    }

    /// <summary>删除用户（audit_logs 以用户名字符串记录，非外键，删除安全；最后一个启用 Admin 禁止删除）</summary>
    [HttpDelete("{id:int}")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<ActionResult<ApiResponse<bool>>> Delete(int id, CancellationToken ct)
    {
        var user = await _store.FindByIdAsync(id, ct);
        if (user is null)
            return NotFound(ApiResponse<bool>.Fail("Users", "用户不存在"));
        if (!await CanRemoveAdminAsync(user, ct))
            return BadRequest(ApiResponse<bool>.Fail("Users", "不能降级/停用/删除最后一个启用的 Admin，避免管理锁定"));

        var ok = await _store.DeleteAsync(id, ct);
        if (!ok)
            return NotFound(ApiResponse<bool>.Fail("Users", "用户不存在"));

        return Ok(ApiResponse<bool>.Ok(true));
    }

    // ═══════════ 自助服务（任何已登录角色） ═══════════

    /// <summary>
    /// 当前登录用户信息（任何已登录角色可调）。供前端显示用户名/角色并做菜单门控；
    /// 与用户管理列表同构（不含密码哈希），只是定位为「自己」。
    /// </summary>
    [HttpGet("me")]
    [Authorize]
    public async Task<ActionResult<ApiResponse<UserDto>>> Me(CancellationToken ct)
    {
        var username = User.Identity?.Name;
        if (string.IsNullOrEmpty(username))
            return Unauthorized(ApiResponse<UserDto>.Fail("Users", "未认证"));

        var user = await _store.FindByUsernameAsync(username, ct);
        if (user is null)
            return Unauthorized(ApiResponse<UserDto>.Fail("Users", "用户不存在"));

        return Ok(ApiResponse<UserDto>.Ok(Map(user)));
    }

    /// <summary>
    /// 自助改密：校验当前密码后更新自己的密码（任何已登录角色可用，不需 Admin）。
    /// 密码变更即时生效，旧 Token 自然过期。
    /// </summary>
    [HttpPut("me/password")]
    [Authorize]
    public async Task<ActionResult<ApiResponse<UserDto>>> ChangeMyPassword(
        [FromBody] ChangeMyPasswordRequest req, CancellationToken ct)
    {
        var username = User.Identity?.Name;
        if (string.IsNullOrEmpty(username))
            return Unauthorized(ApiResponse<UserDto>.Fail("Users", "未认证"));

        var user = await _store.FindByUsernameAsync(username, ct);
        if (user is null)
            return Unauthorized(ApiResponse<UserDto>.Fail("Users", "用户不存在"));

        if (_hasher.VerifyHashedPassword(user, user.PasswordHash, req.CurrentPassword ?? "")
            == PasswordVerificationResult.Failed)
            return BadRequest(ApiResponse<UserDto>.Fail("Users", "当前密码错误"));

        var newPassword = req.NewPassword ?? "";
        if (newPassword.Length < PasswordMinLength)
            return BadRequest(ApiResponse<UserDto>.Fail("Users", $"新密码长度不能少于 {PasswordMinLength} 位"));

        var hash = _hasher.HashPassword(user, newPassword);
        await _store.UpdatePasswordHashAsync(user.Id, hash, ct);

        var updated = await _store.FindByIdAsync(user.Id, ct);
        return Ok(ApiResponse<UserDto>.Ok(Map(updated!)));
    }

    // ═══════════ 工具 ═══════════

    /// <summary>
    /// 判定目标 Admin 是否可被移除（降级/停用/删除）。若目标是当前唯一启用的 Admin 则禁止，
    /// 防止管理接口把所有 Admin 停掉后无人可恢复（用户量小，直接 List 后内存统计，无性能问题）。
    /// </summary>
    private async Task<bool> CanRemoveAdminAsync(UserAccount target, CancellationToken ct)
    {
        if (target.Role != Roles.Admin)
            return true;
        var users = await _store.ListAsync(ct);
        var enabledAdmins = users.Count(u => u.Role == Roles.Admin && u.IsEnabled);
        return enabledAdmins > 1 || (enabledAdmins == 1 && !target.IsEnabled);
    }

    private static bool IsValidRole(string role)
        => role is Roles.Admin or Roles.Operator or Roles.Viewer;

    private static UserDto Map(UserAccount u) => new()
    {
        Id = u.Id,
        Username = u.Username,
        Role = u.Role,
        IsEnabled = u.IsEnabled,
        CreatedAt = u.CreatedAt.ToString("O"),
        UpdatedAt = u.UpdatedAt.ToString("O"),
        LastLoginAt = u.LastLoginAt?.ToString("O")
    };
}

/// <summary>用户 DTO（管理页展示；刻意不含密码哈希/明文）</summary>
public sealed class UserDto
{
    public int Id { get; set; }
    public string Username { get; set; } = "";
    public string Role { get; set; } = "";
    public bool IsEnabled { get; set; }
    public string CreatedAt { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
    public string? LastLoginAt { get; set; }
}

/// <summary>新增用户请求</summary>
public sealed class CreateUserRequest
{
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? Role { get; set; }
}

/// <summary>改角色请求</summary>
public sealed class ChangeRoleRequest
{
    public string? Role { get; set; }
}

/// <summary>启停请求</summary>
public sealed class SetEnabledRequest
{
    public bool IsEnabled { get; set; }
}

/// <summary>Admin 重置密码请求</summary>
public sealed class ResetPasswordRequest
{
    public string? NewPassword { get; set; }
}

/// <summary>自助改密请求（需校验当前密码）</summary>
public sealed class ChangeMyPasswordRequest
{
    public string? CurrentPassword { get; set; }
    public string? NewPassword { get; set; }
}
