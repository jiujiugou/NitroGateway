namespace NitroGateway.Security;

/// <summary>预定义角色常量</summary>
public static class Roles
{
    /// <summary>管理员：设备增删、配置修改、写操作、安全管理</summary>
    public const string Admin = "Admin";

    /// <summary>操作员：查看状态、确认告警、导入点位、写操作（需校验）</summary>
    public const string Operator = "Operator";

    /// <summary>观察者：只读仪表盘和历史数据</summary>
    public const string Viewer = "Viewer";
}
