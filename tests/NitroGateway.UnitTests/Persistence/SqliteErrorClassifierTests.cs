using System.Reflection;
using Microsoft.Data.Sqlite;
using NitroGateway.Persistence.Sqlite;
using NitroGateway.Shared;
using Xunit;

namespace NitroGateway.UnitTests.Persistence;

/// <summary>
/// SQLite 错误分类器测试。SqliteException 的构造函数为 internal，无法直接创建，
/// 因此通过真实 SQLite 错误场景来测试——在集成测试层用真实数据库覆盖。
/// 这里测非 SqliteException 的兜底逻辑。
/// </summary>
public class SqliteErrorClassifierTests
{
    /// <summary>经反射构造指定 ErrorCode 的 SqliteException（构造函数非公开）。</summary>
    private static SqliteException MakeSqliteException(int errorCode)
    {
        const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        var twoArg = typeof(SqliteException).GetConstructor(Flags, null, [typeof(string), typeof(int)], null);
        if (twoArg is not null)
            return (SqliteException)twoArg.Invoke(["boom", errorCode]);

        var threeArg = typeof(SqliteException).GetConstructor(Flags, null, [typeof(string), typeof(int), typeof(int)], null);
        if (threeArg is not null)
            return (SqliteException)threeArg.Invoke(["boom", errorCode, errorCode]);

        var signatures = string.Join("; ",
            typeof(SqliteException).GetConstructors(Flags).Select(c => c.ToString()));
        throw new InvalidOperationException($"无法构造 SqliteException。可用构造: {signatures}");
    }

    /// <summary>各 SqliteErrorCode → 对应 OperationalError（13/10/11/5/其它兜底）。</summary>
    [Theory]
    [InlineData(13, "DiskFull", OperationalSeverity.Critical, "磁盘满")]
    [InlineData(10, "StorageError", OperationalSeverity.Error, "I/O 错误")]
    [InlineData(11, "StorageError", OperationalSeverity.Error, "数据库损坏")]
    [InlineData(5, "DatabaseLocked", OperationalSeverity.Error, "数据库锁定")]
    [InlineData(99, "StorageError", OperationalSeverity.Error, "context")]
    public void SqliteException_MapsByErrorCode(
        int errorCode, string expectedCode, OperationalSeverity severity, string messageFragment)
    {
        var err = SqliteErrorClassifier.Classify(MakeSqliteException(errorCode), "context");

        Assert.Equal(ErrorCategory.Storage, err.Category);
        Assert.Equal(expectedCode, err.Code);
        Assert.Equal(severity, err.Severity);
        Assert.Contains(messageFragment, err.Message);
        Assert.Contains("boom", err.Message);
    }

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
