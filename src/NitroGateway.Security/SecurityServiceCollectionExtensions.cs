using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using NitroGateway.Security.Auth;
using NitroGateway.Security.Guard;

namespace NitroGateway.Security;

/// <summary>Security 模块 DI 注册</summary>
public static class SecurityServiceCollectionExtensions
{
    /// <summary>
    /// 注册 JWT 认证、RBAC 授权、写指令门控。
    /// </summary>
    /// <param name="services">服务集合</param>
    /// <param name="jwtConfig">JWT 签发配置</param>
    /// <param name="users">内置用户列表</param>
    public static IServiceCollection AddNitroSecurity(
        this IServiceCollection services,
        JwtConfig jwtConfig,
        IEnumerable<UserConfig> users)
    {
        // ── JWT 认证 ──
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtConfig.SecretKey));

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

                // SignalR：从 query string 读取 access_token
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

        services.AddAuthorization(options =>
        {
            options.AddPolicy("AdminOnly", p => p.RequireRole(Roles.Admin));
            options.AddPolicy("OperatorOrAdmin", p => p.RequireRole(Roles.Operator, Roles.Admin));
            options.AddPolicy("AllRoles", p => p.RequireRole(Roles.Admin, Roles.Operator, Roles.Viewer));
        });

        // ── JWT 配置 ──
        services.AddSingleton(jwtConfig);
        services.AddSingleton(users.ToList().AsReadOnly());

        // ── Token 签发 ──
        services.AddSingleton<TokenGenerator>();

        // ── 写指令门控 ──
        services.AddSingleton<RangeValidator>();
        services.AddSingleton<RateLimitValidator>();
        services.AddSingleton<ModeValidator>();
        services.AddSingleton<WriteGuard>();

        return services;
    }
}
