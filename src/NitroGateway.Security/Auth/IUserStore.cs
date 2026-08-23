using NitroGateway.Shared;

namespace NitroGateway.Security.Auth;

/// <summary>
/// 用户存储接口（ADR-066：用户管理不走全量 Identity，用户在 SQLite users 表）。
/// 接口定义在 Security 模块（纯契约），SQLite 实现位于 Persistence（Dapper），依赖方向 Security ← Persistence。
/// <para><b>契约：</b>所有方法按操作打开独立连接；写入方法成功后即时生效（无需重启），
/// 登录/授权热路径每次从存储读取，保证「新增/改密/启停」实时可见。</para>
/// <para><b>安全约束：</b>密码以 PasswordHasher 哈希落库；本接口与实现均不得暴露明文。</para>
/// </summary>
public interface IUserStore
{
    /// <summary>按用户名精确查找；不存在返回 null（登录校验第一步）</summary>
    Task<UserAccount?> FindByUsernameAsync(string username, CancellationToken ct = default);

    /// <summary>按主键查找；不存在返回 null（管理接口定位目标）</summary>
    Task<UserAccount?> FindByIdAsync(int id, CancellationToken ct = default);

    /// <summary>全量用户列表（管理页展示；数量级为个位数，不做分页）</summary>
    Task<IReadOnlyList<UserAccount>> ListAsync(CancellationToken ct = default);

    /// <summary>
    /// 新增用户。用户名冲突（唯一约束）返回 <see cref="OperationResult{T}.Failure"/>；
    /// 成功返回含自增 Id 的完整账号。
    /// </summary>
    Task<OperationResult<UserAccount>> CreateAsync(
        string username, string passwordHash, string role, CancellationToken ct = default);

    /// <summary>改角色（角色必须是预定义之一，调用方校验）</summary>
    Task<bool> UpdateRoleAsync(int id, string role, CancellationToken ct = default);

    /// <summary>启停（IsEnabled 即时生效，下次登录按状态拒绝）</summary>
    Task<bool> UpdateEnabledAsync(int id, bool isEnabled, CancellationToken ct = default);

    /// <summary>重置密码（写入新哈希；旧 Token 自然过期，不主动吊销）</summary>
    Task<bool> UpdatePasswordHashAsync(int id, string passwordHash, CancellationToken ct = default);

    /// <summary>登录成功时刷新最近登录时间（供管理页展示）</summary>
    Task<bool> UpdateLastLoginAsync(int id, DateTime lastLoginAt, CancellationToken ct = default);

    /// <summary>删除用户（audit_logs 以用户名字符串记录，非外键，删除安全）</summary>
    Task<bool> DeleteAsync(int id, CancellationToken ct = default);

    /// <summary>
    /// 首启种子：users 表为空时把配置用户（<see cref="UserConfig"/>）灌入，返回实际插入数；
    /// 表非空则原样返回 0（配置仅引导，不再承载运行时账号）。单事务批量插入，避免半种子。
    /// </summary>
    Task<int> SeedIfEmptyAsync(IReadOnlyList<UserConfig> configUsers, CancellationToken ct = default);
}
