using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NitroGateway.Security;
using NitroGateway.Webapi.Controllers;
using NitroGateway.Webapi.Hubs;
using Xunit;

namespace NitroGateway.UnitTests;

/// <summary>ADR-022 P1-1/P1-2：Hub 鉴权与 DevicesController 写操作 RBAC 收窄（反射断言，防止回退）</summary>
public class WebapiAuthorizationTests
{
    [Fact]
    public void LiveDataHub_RequiresAuthentication()
    {
        // P1-1：Hub 必须 [Authorize]，禁止匿名订阅实时数据
        Assert.NotNull(typeof(LiveDataHub).GetCustomAttribute<AuthorizeAttribute>());
    }

    [Fact]
    public void DevicesController_MutatingActions_ExcludeViewer()
    {
        // P1-2：Viewer 只读——所有 POST/PUT/DELETE 动作有效角色不得含 Viewer
        var mutating = new[] { typeof(HttpPostAttribute), typeof(HttpPutAttribute), typeof(HttpDeleteAttribute) };
        foreach (var method in typeof(DevicesController).GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            var verbs = method.GetCustomAttributes().Where(a => mutating.Any(t => t.IsInstanceOfType(a))).ToList();
            if (verbs.Count == 0) continue;

            var roles = EffectiveRoles(method);
            var roleList = roles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            Assert.DoesNotContain(Roles.Viewer, roleList);
        }
    }

    [Fact]
    public void DevicesController_ReadActions_RemainAllRoles()
    {
        // 只读 GET 仍需 Viewer 可访问（仪表盘/历史数据）
        foreach (var method in typeof(DevicesController).GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            if (method.GetCustomAttribute<HttpGetAttribute>() is null) continue;
            var roles = EffectiveRoles(method);
            var roleList = roles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            Assert.Contains(Roles.Viewer, roleList);
        }
    }

    [Fact]
    public void WriteScopedControllers_ExcludeViewer()
    {
        // 告警规则/死信/点位导入控制器类级均为 Admin+Operator
        foreach (var type in new[] { typeof(AlarmRulesController), typeof(PointImportController), typeof(AuditLogsController) })
        {
            var roles = type.GetCustomAttribute<AuthorizeAttribute>()?.Roles ?? "";
            var roleList = roles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            Assert.DoesNotContain(Roles.Viewer, roleList);
        }
    }

    [Fact]
    public void AuditLogsController_RequiresAdminOperator()
    {
        // ADR-065 A3：操作审计属敏感数据，仅 Admin/Operator 可查（Viewer 不可见）
        var roles = typeof(AuditLogsController).GetCustomAttribute<AuthorizeAttribute>()?.Roles ?? "";
        var roleList = roles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Contains(Roles.Admin, roleList);
        Assert.Contains(Roles.Operator, roleList);
        Assert.DoesNotContain(Roles.Viewer, roleList);
    }

    [Fact]
    public void UserController_AdminActions_RequireAdminOnly()
    {
        // ADR-066：用户管理接口仅 Admin（列表/新增/改角色/启停/重置密码/删除）；
        // 自助改密（me/password）单独断言，需对所有已登录角色开放
        var mutating = new[] { typeof(HttpPostAttribute), typeof(HttpPutAttribute), typeof(HttpDeleteAttribute) };
        foreach (var method in typeof(UserController).GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            var verbs = method.GetCustomAttributes().Where(a => mutating.Any(t => t.IsInstanceOfType(a))).ToList();
            if (verbs.Count == 0 || method.Name == nameof(UserController.ChangeMyPassword)) continue;

            Assert.Equal("AdminOnly", method.GetCustomAttribute<AuthorizeAttribute>()?.Policy);
        }
    }

    [Fact]
    public void UserController_List_RequiresAdminOnly()
    {
        // 用户列表属敏感账号数据，仅 Admin 可查（Viewer 不可见）
        var method = typeof(UserController).GetMethod(nameof(UserController.List))!;
        Assert.Equal("AdminOnly", method.GetCustomAttribute<AuthorizeAttribute>()?.Policy);
    }

    [Fact]
    public void UserController_SelfPassword_RequiresAuthenticationNotAdminOnly()
    {
        // 自助改密需对任意已登录角色开放——不能带 AdminOnly 策略（否则改密码也要 Admin）
        var method = typeof(UserController).GetMethod(nameof(UserController.ChangeMyPassword))!;
        var auth = method.GetCustomAttribute<AuthorizeAttribute>();
        Assert.NotNull(auth);
        Assert.Null(auth!.Policy);
        Assert.Null(auth.Roles);
    }

    [Fact]
    public void UserController_Me_RequiresAuthenticationNotAdminOnly()
    {
        // GET /api/user/me 供任意已登录角色读取自己的用户名/角色（前端菜单门控），不能带 AdminOnly
        var method = typeof(UserController).GetMethod(nameof(UserController.Me))!;
        var auth = method.GetCustomAttribute<AuthorizeAttribute>();
        Assert.NotNull(auth);
        Assert.Null(auth!.Policy);
        Assert.Null(auth.Roles);
    }

    private static string EffectiveRoles(MethodInfo method)
        => method.GetCustomAttribute<AuthorizeAttribute>()?.Roles
           ?? method.DeclaringType!.GetCustomAttribute<AuthorizeAttribute>()?.Roles
           ?? "";
}
