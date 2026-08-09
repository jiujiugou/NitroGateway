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
        foreach (var type in new[] { typeof(AlarmRulesController), typeof(DeadLettersController), typeof(PointImportController) })
        {
            var roles = type.GetCustomAttribute<AuthorizeAttribute>()?.Roles ?? "";
            var roleList = roles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            Assert.DoesNotContain(Roles.Viewer, roleList);
        }
    }

    private static string EffectiveRoles(MethodInfo method)
        => method.GetCustomAttribute<AuthorizeAttribute>()?.Roles
           ?? method.DeclaringType!.GetCustomAttribute<AuthorizeAttribute>()?.Roles
           ?? "";
}
