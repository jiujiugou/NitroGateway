using NitroGateway.Persistence.Sqlite;
using NitroGateway.Shared;
using Xunit;

namespace NitroGateway.UnitTests;

/// <summary>
/// SQLite 错误分类器测试。SqliteException 的构造函数为 internal，无法直接创建，
/// 因此通过真实 SQLite 错误场景来测试——在集成测试层用真实数据库覆盖。
/// 这里测非 SqliteException 的兜底逻辑。
/// </summary>
public class SqliteErrorClassifierTests
{
    /// <summary>非 SqliteException → Storage / Error（兜底）</summary>
    [Fact]
    public void NonSqliteException_MapsToStorage()
    {
        var ex = new InvalidOperationException("some error");
        var err = SqliteErrorClassifier.Classify(ex, "操作失败");
        Assert.Equal(ErrorCategory.Storage, err.Category);
        Assert.Equal(OperationalSeverity.Error, err.Severity);
        Assert.Contains("操作失败", err.Message);
        Assert.Contains("some error", err.Message);
    }

    /// <summary>NullReferenceException 也应映射为 Storage 错误</summary>
    [Fact]
    public void NullRefException_MapsToStorage()
    {
        var ex = new NullReferenceException("null ref");
        var err = SqliteErrorClassifier.Classify(ex, "读取");
        Assert.Equal(ErrorCategory.Storage, err.Category);
    }

    /// <summary>TaskCanceledException 也应映射为 Storage 错误</summary>
    [Fact]
    public void TaskCancelledException_MapsToStorage()
    {
        var ex = new TaskCanceledException("cancelled");
        var err = SqliteErrorClassifier.Classify(ex, "查询");
        Assert.Equal(ErrorCategory.Storage, err.Category);
    }
}
