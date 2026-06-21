using NitroGateway.DeviceManagement;
using NitroGateway.Infrastructure.Sqlite;
using NitroGateway.Webapi.Hubs;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.WithOrigins("http://localhost:5173").AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

var dbPath = Path.GetFullPath(args.Length > 0 ? args[0] : "nitrogateway.db");
builder.Services.AddNitroSqlite($"Data Source={dbPath}");
builder.Services.AddNitroDevice();

builder.Services.AddSignalR();
builder.Services.AddControllers();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<NitroGatewayDbContext>();
    await db.Database.EnsureCreatedAsync();
}
MigrationRunner.Run($"Data Source={dbPath}");

app.UseCors();
app.MapControllers();
app.MapHub<LiveDataHub>("/hubs/live");

app.Run();
