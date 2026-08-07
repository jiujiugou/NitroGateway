using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Text;

namespace NitroGateway.Persistence
{
    public static class DatabaseInitializationExtensions
    {
        public static WebApplication InitializeDatabase(this WebApplication app)
        {
            var configuration = app.Services.GetRequiredService<IConfiguration>();

            var conn = configuration.GetValue<string>("Persistence:ConnectionString")
                ?? throw new InvalidOperationException();

            // ADR-002 P3-2：从 DI 取日志器传入，迁移/备份日志不再丢弃
            var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("NitroGateway.Persistence.MigrationRunner");
            MigrationRunner.Run(conn, logger);

            return app;
        }
    }
}
