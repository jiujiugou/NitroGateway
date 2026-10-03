using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging.Abstractions;
using NitroGateway.Security.Audit;
using NitroGateway.Shared;
using Xunit;

namespace NitroGateway.UnitTests.Security;

/// <summary>AuditMiddleware / ExceptionHandlingMiddleware 单测。</summary>
public class MiddlewareTests
{
    private sealed class FakeAuditStore : IAuditLogStore
    {
        public List<AuditLogEntry> Written { get; } = [];
        public bool Throw { get; set; }

        public Task WriteAsync(AuditLogEntry entry, CancellationToken ct = default)
        {
            if (Throw) throw new InvalidOperationException("db busy");
            Written.Add(entry);
            return Task.CompletedTask;
        }

        public Task<OperationResult<AuditLogQueryResult>> QueryAsync(AuditLogQuery query, CancellationToken ct = default)
            => Task.FromResult(OperationResult<AuditLogQueryResult>.Success(new AuditLogQueryResult()));
    }

    private static HttpContext Context(string method, string path, int statusCode = 200)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        ctx.Request.Path = path;
        ctx.Response.StatusCode = statusCode;
        ctx.Connection.RemoteIpAddress = IPAddress.Parse("1.2.3.4");
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, "admin"),
            new Claim(ClaimTypes.Role, "Admin")
        ]));
        return ctx;
    }

    // ── AuditMiddleware ──

    [Fact]
    public async Task Audit_NonApiPath_NotPersisted()
    {
        var store = new FakeAuditStore();
        var mw = new AuditMiddleware(_ => Task.CompletedTask, NullLogger<AuditMiddleware>.Instance);

        await mw.InvokeAsync(Context("POST", "/health"), store);

        Assert.Empty(store.Written);
    }

    [Fact]
    public async Task Audit_GetApiRequest_NotPersisted()
    {
        var store = new FakeAuditStore();
        var mw = new AuditMiddleware(_ => Task.CompletedTask, NullLogger<AuditMiddleware>.Instance);

        await mw.InvokeAsync(Context("GET", "/api/devices"), store);

        Assert.Empty(store.Written); // GET 只记日志不落库
    }

    [Fact]
    public async Task Audit_ApiErrorStatus_NotPersisted()
    {
        var store = new FakeAuditStore();
        var mw = new AuditMiddleware(_ => Task.CompletedTask, NullLogger<AuditMiddleware>.Instance);

        await mw.InvokeAsync(Context("POST", "/api/commands", statusCode: 400), store);

        Assert.Empty(store.Written); // >=400 只告警不落库
    }

    [Fact]
    public async Task Audit_PostApiSuccess_PersistsEntryWithClaimsAndStatus()
    {
        var store = new FakeAuditStore();
        var mw = new AuditMiddleware(_ => Task.CompletedTask, NullLogger<AuditMiddleware>.Instance);

        await mw.InvokeAsync(Context("POST", "/api/commands", statusCode: 200), store);

        var e = Assert.Single(store.Written);
        Assert.Equal("admin", e.User);
        Assert.Equal("Admin", e.Role);
        Assert.Equal("POST", e.Method);
        Assert.Equal("/api/commands", e.Path);
        Assert.Equal(200, e.StatusCode);
        Assert.Equal("1.2.3.4", e.Ip);
    }

    [Fact]
    public async Task Audit_AnonymousRequest_UsesFallbackUserAndRole()
    {
        var store = new FakeAuditStore();
        var mw = new AuditMiddleware(_ => Task.CompletedTask, NullLogger<AuditMiddleware>.Instance);
        var ctx = Context("PUT", "/api/devices", statusCode: 200);
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity());

        await mw.InvokeAsync(ctx, store);

        var e = Assert.Single(store.Written);
        Assert.Equal("anonymous", e.User);
        Assert.Equal("-", e.Role);
    }

    [Fact]
    public async Task Audit_StoreThrows_IsSwallowed()
    {
        var store = new FakeAuditStore { Throw = true };
        var mw = new AuditMiddleware(_ => Task.CompletedTask, NullLogger<AuditMiddleware>.Instance);

        await mw.InvokeAsync(Context("POST", "/api/commands", statusCode: 200), store); // 不得抛出
    }

    // ── ExceptionHandlingMiddleware ──

    [Fact]
    public async Task Exception_NextSucceeds_PassesThrough()
    {
        var ctx = Context("GET", "/api/x", statusCode: 201);
        var mw = new ExceptionHandlingMiddleware(_ => Task.CompletedTask, NullLogger<ExceptionHandlingMiddleware>.Instance);

        await mw.InvokeAsync(ctx);

        Assert.Equal(201, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task Exception_NextThrows_Writes500Json()
    {
        var ctx = Context("POST", "/api/x");
        ctx.Response.Body = new MemoryStream();
        var mw = new ExceptionHandlingMiddleware(
            _ => throw new InvalidOperationException("boom"),
            NullLogger<ExceptionHandlingMiddleware>.Instance);

        await mw.InvokeAsync(ctx);

        Assert.Equal(StatusCodes.Status500InternalServerError, ctx.Response.StatusCode);
        Assert.Contains("application/json", ctx.Response.ContentType);
        ctx.Response.Body.Position = 0;
        var body = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
        Assert.Contains("InternalError", body);
    }

    [Fact]
    public async Task Exception_ResponseAlreadyStarted_Rethrows()
    {
        var ctx = Context("POST", "/api/x");
        ctx.Features.Set<IHttpResponseFeature>(new StartedResponseFeature());
        var mw = new ExceptionHandlingMiddleware(
            _ => throw new InvalidOperationException("late boom"),
            NullLogger<ExceptionHandlingMiddleware>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => mw.InvokeAsync(ctx));
    }

    private sealed class StartedResponseFeature : IHttpResponseFeature
    {
        public int StatusCode { get; set; } = 200;
        public string? ReasonPhrase { get; set; }
        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
        public Stream Body { get; set; } = new MemoryStream();
        public bool HasStarted => true;
        public void OnStarting(Func<object, Task> callback, object state) { }
        public void OnCompleted(Func<object, Task> callback, object state) { }
    }
}
