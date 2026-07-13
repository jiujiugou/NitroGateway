using NitroGateway.Shared;
using Xunit;

namespace NitroGateway.UnitTests;

/// <summary>
/// OperationalError 工厂方法测试。
/// 验证每个工厂方法正确设置了 Category、Severity、Code。
/// DataDispatcher 依赖这些字段做错误路由（Severity>=Error → LogError）。
/// </summary>
public class OperationalErrorTests
{
    /// <summary>StorageFull 应为 Storage 类别 + Critical 严重性</summary>
    [Fact]
    public void StorageFull_HasCorrectCategoryAndSeverity()
    {
        var err = OperationalError.StorageFull("磁盘满");
        Assert.Equal(ErrorCategory.Storage, err.Category);
        Assert.Equal(OperationalSeverity.Critical, err.Severity);
        Assert.Equal("DiskFull", err.Code);
    }

    /// <summary>DatabaseLocked 应为 Storage 类别 + Error 严重性</summary>
    [Fact]
    public void DatabaseLocked_HasCorrectCategoryAndSeverity()
    {
        var err = OperationalError.DatabaseLocked("数据库锁定");
        Assert.Equal(ErrorCategory.Storage, err.Category);
        Assert.Equal(OperationalSeverity.Error, err.Severity);
        Assert.Equal("DatabaseLocked", err.Code);
    }

    /// <summary>Timeout 应为 Communication 类别 + Warning 严重性</summary>
    [Fact]
    public void Timeout_HasCorrectCategoryAndSeverity()
    {
        var err = OperationalError.Timeout("超时");
        Assert.Equal(ErrorCategory.Communication, err.Category);
        Assert.Equal(OperationalSeverity.Warning, err.Severity);
    }

    /// <summary>Protocol 应为 Protocol 类别 + Warning 严重性</summary>
    [Fact]
    public void Protocol_HasCorrectCategory()
    {
        var err = OperationalError.Protocol("校验失败");
        Assert.Equal(ErrorCategory.Protocol, err.Category);
    }

    /// <summary>Validation 应为 Validation 类别</summary>
    [Fact]
    public void Validation_HasCorrectCategory()
    {
        var err = OperationalError.Validation("参数无效");
        Assert.Equal(ErrorCategory.Validation, err.Category);
        Assert.Equal(OperationalSeverity.Warning, err.Severity);
    }

    /// <summary>Unavailable 应为 Resource 类别 + Error 严重性</summary>
    [Fact]
    public void Unavailable_HasCorrectCategory()
    {
        var err = OperationalError.Unavailable("未连接");
        Assert.Equal(ErrorCategory.Resource, err.Category);
        Assert.Equal(OperationalSeverity.Error, err.Severity);
    }

    /// <summary>NotFound 应为 Info 严重性（信息性）</summary>
    [Fact]
    public void NotFound_IsInfoSeverity()
    {
        var err = OperationalError.NotFound("死信不存在");
        Assert.Equal(OperationalSeverity.Info, err.Severity);
    }

    /// <summary>General 应有默认 General 类别 + Warning 严重性</summary>
    [Fact]
    public void General_HasDefaultCategory()
    {
        var err = OperationalError.General("未知错误");
        Assert.Equal(ErrorCategory.General, err.Category);
        Assert.Equal(OperationalSeverity.Warning, err.Severity);
    }
}
