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

        // ADR-052 问题2：非开发环境拒绝仍使用默认测试账号密码（admin/admin123）启动，
        // 与 JWT 的 ChangeMe 拒绝同思路——防止 appsettings 内置测试账号直接带上生产；
        // 生产请用环境变量 Security__Users__N__Password 覆盖（compose 已强制 ADMIN_PASSWORD）。
        if (!IsDevelopment(configuration))
        {
            var hasher = new PasswordHasher<UserConfig>();
            foreach (var user in jwtConfig.Users)
            {
                foreach (var defaultPwd in DefaultTestPasswords)
                {
                    try
                    {
                        if (hasher.VerifyHashedPassword(user, user.Password, defaultPwd) != PasswordVerificationResult.Failed)
                            throw new InvalidOperationException(
                                $"Security:Users 账号 {user.Username} 仍使用默认测试密码（{defaultPwd}），禁止用于生产：请通过环境变量 Security__Users__N__Password 覆盖");
                    }
                    catch (FormatException)
                    {
                        // 非 PasswordHasher 标准哈希（如外部迁移哈希），跳过默认密码比对，登录时自行校验
                    }
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
}
