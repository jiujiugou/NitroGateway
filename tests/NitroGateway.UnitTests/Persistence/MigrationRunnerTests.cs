using NitroGateway.Persistence;
using Xunit;

namespace NitroGateway.UnitTests.Persistence;

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
