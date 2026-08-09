using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Text;

namespace NitroGateway.Persistence
{
    /// <summary>
    /// WebApplication 启动期数据库初始化扩展。
    /// 在应用启动时同步执行 FluentMigrator 迁移与迁移前备份，
    /// 保证首次请求到达前库结构已就绪。
    /// </summary>
    public static class DatabaseInitializationExtensions
    {
        /// <summary>
        /// 初始化数据库：读取 <c>Persistence:ConnectionString</c> 配置并执行迁移。
        /// 连接串缺失时抛出 <see cref= InvalidOperationException/>（配置错误应快速失败）。
        /// </summary>
        /// <param name=app>已构建的 WebApplication 实例</param>
        /// <returns>原应用实例，便于链式调用</returns>
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
