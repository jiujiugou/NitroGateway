using FluentMigrator.Runner;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.Sqlite;

namespace NitroGateway.Persistence;

/// <summary>FluentMigrator 迁移执行器，启动时调用一次</summary>
public static class MigrationRunner
{
    /// <summary>执行所有待运行迁移（幂等：已执行过的跳过）</summary>
    public static void Run(string connectionString)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();

        var services = new ServiceCollection()
            .AddFluentMigratorCore()
            .ConfigureRunner(rb => rb
                .AddSQLite()
                .WithGlobalConnectionString(connectionString)
                .ScanIn(typeof(MigrationRunner).Assembly).For.Migrations())
            .BuildServiceProvider();

        using var scope = services.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();
        runner.MigrateUp();
    }
}
