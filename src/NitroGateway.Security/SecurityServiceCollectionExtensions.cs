using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using NitroGateway.Security.Auth;
using NitroGateway.Security.Guard;

namespace NitroGateway.Security;

/// <summary>Security 模块 DI 注册</summary>
public static class SecurityServiceCollectionExtensions
{
    /// <summary>从 IConfiguration 读取 JWT 配置和用户信息，注册认证+授权+门控</summary>
    public static IServiceCollection AddNitroSecurity(this IServiceCollection services, IConfiguration configuration)
    {
        // ── 1. 配置绑定 ──
        var jwtConfig = configuration.GetSection(JwtConfig.SectionName).Get<JwtConfig>() ?? new JwtConfig();

        if (string.IsNullOrWhiteSpace(jwtConfig.JwtSecretKey) ||
            jwtConfig.JwtSecretKey.StartsWith("NitroGateway-Dev"))
        {
            // 开发便利：自动生成随机密钥（每次启动变化，token 不跨重启持久）
            jwtConfig = new JwtConfig
            {
                JwtSecretKey = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
                Issuer = jwtConfig.Issuer,
                Audience = jwtConfig.Audience,
                ExpireHours = jwtConfig.ExpireHours,
                Users = jwtConfig.Users
            };
        }

        // ADR-004 P2-2：fail-fast 校验密钥强度与有效期，弱配置直接拒绝启动
        if (Encoding.UTF8.GetByteCount(jwtConfig.JwtSecretKey) < 32)
            throw new InvalidOperationException("Security:JwtSecretKey 长度不足 32 字节，请配置强密钥后启动");

        // ADR-022 P1-5：仓库内公开占位符（如 docker-compose 曾回退的 Production-ChangeMe）一律拒绝启动，
        // 防止"忘了设置 JWT_SECRET"时用公开密钥上线，被离线伪造 Admin token
        if (jwtConfig.JwtSecretKey.Contains("ChangeMe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Security:JwtSecretKey 含公开占位符（ChangeMe），禁止用于生产，请配置强密钥后启动");

        if (jwtConfig.ExpireHours < 1)
            throw new InvalidOperationException("Security:ExpireHours 必须 ≥ 1 小时");

        // ADR-004 P2-3：配置角色必须是预定义角色之一，防止任意字符串签发越权 token
        foreach (var user in jwtConfig.Users)
        {
            if (user.Role is not (Roles.Admin or Roles.Operator or Roles.Viewer))
                throw new InvalidOperationException($"Security:Users 角色无效: {user.Username} → {user.Role}（允许 Admin/Operator/Viewer）");
        }

        // —— 密码归一化：环境变量常以明文覆盖 Security__Users__N__Password ——
        // TokenGenerator 只认 PasswordHasher 哈希（ADR-004 P1-2 已移除明文 Equals 回退），
        // 明文若直接交给登录会 500（VerifyHashedPassword 抛 Base-64 解析异常）。
        // 这里在配置加载阶段统一处理：非 PasswordHasher 哈希一律按明文 → 生产拒绝默认测试密码 → 哈希化写回，登录逻辑不变。
        // （2026-08-19 容器实测：修复前 compose 明文覆盖后登录 500；修复后明文密码可正常登录。）
        var hasher = new PasswordHasher<UserConfig>();
        for (var i = 0; i < jwtConfig.Users.Count; i++)
        {
            var user = jwtConfig.Users[i];
            if (IsHashedPassword(user.Password))
                continue; // 已是 PasswordHasher 哈希，交给下方生产默认密码守卫

            if (!IsDevelopment(configuration))
            {
                foreach (var defaultPwd in DefaultTestPasswords)
                {
                    if (string.Equals(user.Password, defaultPwd, StringComparison.Ordinal))
                        throw new InvalidOperationException(
                            $"Security:Users 账号 {user.Username} 仍使用默认测试密码（{defaultPwd}），禁止用于生产：请通过环境变量 Security__Users__N__Password 覆盖");
                }
            }

            // 明文强密码（或外部非 PasswordHasher 格式）→ 哈希化写回，登录按哈希校验
            jwtConfig.Users[i] = new UserConfig
            {
                Username = user.Username,
                Password = hasher.HashPassword(user, user.Password),
                Role = user.Role
            };
        }

        // ADR-052 问题2：非开发环境拒绝仍使用默认测试账号密码（admin/admin123 等）启动，
        // 与 JWT 的 ChangeMe 拒绝同思路——防止 appsettings 内置测试账号直接带上生产。
        // 生产请用环境变量 Security__Users__N__Password 覆盖（compose 已强制 ADMIN/OPERATOR/VIEWER_PASSWORD）。
        // 此处所有 Password 均已归一化为哈希；明文默认密码已在上方拒绝。
        if (!IsDevelopment(configuration))
        {
            foreach (var user in jwtConfig.Users)
            {
                foreach (var defaultPwd in DefaultTestPasswords)
                {
                    if (hasher.VerifyHashedPassword(user, user.Password, defaultPwd) != PasswordVerificationResult.Failed)
                        throw new InvalidOperationException(
                            $"Security:Users 账号 {user.Username} 仍使用默认测试密码（{defaultPwd}），禁止用于生产：请通过环境变量 Security__Users__N__Password 覆盖");
                }
            }
        }

        services.AddSingleton(jwtConfig);
        services.AddSingleton<IReadOnlyList<UserConfig>>(jwtConfig.Users);

        // ── 2. Token 签发 ──
        services.AddSingleton<TokenGenerator>();

        // ── 3. JWT 认证中间件 ──
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtConfig.JwtSecretKey));

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ValidIssuer = jwtConfig.Issuer,
                    ValidAudience = jwtConfig.Audience,
                    IssuerSigningKey = key
                };

                // SignalR 从 query string 读取 access_token
                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        var accessToken = context.Request.Query["access_token"];
                        if (!string.IsNullOrEmpty(accessToken))
                            context.Token = accessToken;
                        return Task.CompletedTask;
                    }
                };
            });

        // ── 4. RBAC 授权策略 ──
        services.AddAuthorization(options =>
        {
            options.AddPolicy("AdminOnly", p => p.RequireRole(Roles.Admin));
            options.AddPolicy("OperatorOrAdmin", p => p.RequireRole(Roles.Operator, Roles.Admin));
            options.AddPolicy("AllRoles", p => p.RequireRole(Roles.Admin, Roles.Operator, Roles.Viewer));
        });

        // ── 5. 写指令门控（ADR-004 P1-1：预留能力，未接线）──
        // Webapi 当前无写端点，Modbus/S7 驱动 WriteAsync 无生产调用方；
        // 启用需新增写端点 + WriteGuard.Evaluate 接入 + 驱动 WriteAsync 调用链，docs F-28 已同步标注
        services.AddSingleton<RangeValidator>();
        services.AddSingleton<RateLimitValidator>();
        services.AddSingleton<ModeValidator>();
        services.AddSingleton<WriteGuard>();

        // ── 6. 登录失败限流（ADR-004 P2-1，内存实现，内网防爆破最小平卫）──
        services.AddSingleton<LoginRateLimiter>();

        return services;
    }

    /// <summary>
    /// 默认测试账号明文密码（与 appsettings 内置测试账号一致，生产禁止使用）。
    /// 新增测试账号默认密码时需同步补充此处，供生产守卫比对。
    /// </summary>
    private static readonly string[] DefaultTestPasswords = ["admin123", "oper123", "view123"];

    /// <summary>
    /// 是否开发环境。WebApplication/主机默认环境为 Production；
    /// 未显式设置任一环境变量时视为非开发（按 Production 行为对待，fail-safe）。
    /// </summary>
    private static bool IsDevelopment(IConfiguration configuration)
        => string.Equals(configuration["ASPNETCORE_ENVIRONMENT"], "Development", StringComparison.OrdinalIgnoreCase)
           || string.Equals(configuration["DOTNET_ENVIRONMENT"], "Development", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 是否为 PasswordHasher 标准哈希：Base64 可解析且首字节为版本标记（0x00 V2 / 0x01 V3）。
    /// 非 Base64（明文密码）或版本标记不符（外部哈希）一律视为明文，交由上层归一化哈希化，
    /// 因为 TokenGenerator 仅支持 PasswordHasher 校验（ADR-004 P1-2）。
    /// </summary>
    private static bool IsHashedPassword(string password)
    {
        if (string.IsNullOrEmpty(password))
            return false;
        try
        {
            var bytes = Convert.FromBase64String(password);
            return bytes.Length > 0 && (bytes[0] == 0x00 || bytes[0] == 0x01);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
