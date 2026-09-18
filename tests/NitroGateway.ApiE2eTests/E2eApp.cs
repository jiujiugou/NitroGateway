using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace NitroGateway.ApiE2eTests;

/// <summary>
/// L3 API E2E 唯一宿主：WebApplicationFactory 真起 NitroGateway.Webapi 组合根
/// （DI、中间件、SQLite 迁移、用户种子、HostedService）。
/// 每个工厂实例用独立临时 SQLite 文件，Dispose 时删除 → 测试互相隔离、可并行、不留脏库。
/// 共享给整个 "E2E" 集合使用（一次启动，多测试类顺序复用）。
/// </summary>
public sealed class E2eApp : WebApplicationFactory<Program>
{
    private readonly string _dbDir;

    public E2eApp()
    {
        // 相对 cwd 建库会污染 bin，用临时目录 + GUID 保证每实例唯一
        _dbDir = Path.Combine(Path.GetTempPath(), "nitrogateway-e2e", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dbDir);
    }

    /// <summary>库内路径：初始化阶段（迁移前）由 WebApplicationFactory 重新构造 host 时写入 UseSetting。</summary>
    public string DbPath => Path.Combine(_dbDir, "e2e.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // UseSetting = host 配置，优先于 appsettings.json；Program 顶层读取 builder.Configuration 即可拿到。
        // UseEnvironment(Development)：走开发账号种子（admin/admin123），跳过生产强密钥门禁。
        builder.UseEnvironment("Development");
        // IsDevelopment 守卫读的是配置键（非 host env），需显式注入，否则按 Production fail-safe 拒绝测试账号
        builder.UseSetting("DOTNET_ENVIRONMENT", "Development");
        builder.UseSetting("ASPNETCORE_ENVIRONMENT", "Development");
        builder.UseSetting("Persistence:ConnectionString", $"Data Source={DbPath}");
        // E2E 不依赖外部 broker：把 MQTT 指向无监听端口，避免连到 appsettings 默认公网 broker 产生脏外部行为。
        builder.UseSetting("MQTT:Host", "127.0.0.1");
        builder.UseSetting("MQTT:Port", "18998");
        // 收敛日志噪音（Serilog 从配置读取，覆盖最低级别）
        builder.UseSetting("Serilog:MinimumLevel:Default", "Warning");
    }

    public HttpClient CreateAdminClient() => CreateClient();

    /// <summary>以指定角色登录，返回已带 Bearer 头的 HttpClient。</summary>
    public async Task<HttpClient> LoginAsAsync(string username, string password)
    {
        var client = CreateClient();
        var resp = await client.PostAsJsonAsync("/api/auth/login", new { username, password });
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<LoginEnvelope>();
        Assert.NotNull(body?.Data?.Token);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body.Data.Token);
        return client;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { if (Directory.Exists(_dbDir)) Directory.Delete(_dbDir, recursive: true); }
        catch { /* 清理失败不阻断测试结论 */ }
    }
}

/// <summary>登录响应壳（ApiResponse&lt;LoginResponse&gt;）</summary>
public sealed class LoginEnvelope
{
    public bool Success { get; init; }
    public LoginData? Data { get; init; }
}

public sealed class LoginData
{
    public string Token { get; init; } = "";
    public string TokenType { get; init; } = "";
}

/// <summary>共享宿主：整个集合一个工厂实例（启动/迁移只跑一次）。</summary>
[CollectionDefinition("E2E")]
public sealed class E2eCollection : ICollectionFixture<E2eApp>;
