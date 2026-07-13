using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
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
            jwtConfig = new JwtConfig
            {
                JwtSecretKey = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
                Issuer = jwtConfig.Issuer,
                Audience = jwtConfig.Audience,
                ExpireHours = jwtConfig.ExpireHours,
                Users = jwtConfig.Users
            };
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

        // ── 5. 写指令门控 ──
        services.AddSingleton<RangeValidator>();
        services.AddSingleton<RateLimitValidator>();
        services.AddSingleton<ModeValidator>();
        services.AddSingleton<WriteGuard>();

        return services;
    }
}
