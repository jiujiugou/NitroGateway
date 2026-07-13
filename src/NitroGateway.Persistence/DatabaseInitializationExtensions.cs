using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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

            MigrationRunner.Run(conn);

            return app;
        }
    }
}
