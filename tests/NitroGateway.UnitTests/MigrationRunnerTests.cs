using NitroGateway.Persistence;
using Xunit;

namespace NitroGateway.UnitTests;

/// <summary>MigrationRunner 连接串解析测试（ADR-018 P3-6）：Data Source 提取兼容变体。</summary>
public class MigrationRunnerTests
{
    [Theory]
    [InlineData("Data Source=/data/ntg.db", "/data/ntg.db")]
    [InlineData("Data Source = /data/ntg.db", "/data/ntg.db")]
    [InlineData("data source=/data/ntg.db;Cache=Shared", "/data/ntg.db")]
    [InlineData("Mode=ReadWrite;Data Source=C:\\data\\ntg.db", "C:\\data\\ntg.db")]
    public void ExtractDataSource_ParsesVariants(string connectionString, string expected)
    {
        var actual = MigrationRunner.ExtractDataSource(connectionString);
        Assert.Equal(expected, actual);
    }
}
