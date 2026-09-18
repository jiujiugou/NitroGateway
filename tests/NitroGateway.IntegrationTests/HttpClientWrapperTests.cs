using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using NitroGateway.Shared;
using NitroGateway.Transport.HTTP;
using Xunit;

namespace NitroGateway.IntegrationTests;

public class HttpClientWrapperTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public int Calls { get; private set; }

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        public void SetRespond(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(_respond(request));
        }
    }

    private static HttpConnectionOptions Options(int maxRetries = 3) => new()
    {
        BaseUrl = "http://localhost",
        MaxRetries = maxRetries,
        RetryBackoffBaseMs = 1,
        TimeoutMs = 30_000,
        HealthPath = "/health"
    };

    private static HttpResponseMessage Ok() => new(HttpStatusCode.OK) { Content = new StringContent("ok") };

    private static HttpResponseMessage ServerError() => new(HttpStatusCode.InternalServerError) { Content = new StringContent("boom") };

    [Fact]
    public async Task HealthCheck_Success_SetsConnected()
    {
        var handler = new StubHandler(_ => Ok());
        var wrapper = new HttpClientWrapper(Options(), NullLogger<HttpClientWrapper>.Instance, handler);
        var states = new List<HttpConnectionState>();
        wrapper.StateChanged += s => states.Add(s);

        Assert.True((await wrapper.HealthCheckAsync()).IsSuccess);
        Assert.Equal(HttpConnectionState.Connected, wrapper.State);
        Assert.Equal([HttpConnectionState.Connected], states);
    }

    [Fact]
    public async Task HttpRequestException_ClassifiedAsCommunication()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("connection refused"));
        var wrapper = new HttpClientWrapper(Options(maxRetries: 2), NullLogger<HttpClientWrapper>.Instance, handler);

        var result = await wrapper.SendAsync(new HttpRequest { Path = "/api/x", Method = HttpMethod.Get });

        Assert.True(result.IsFailure);
        Assert.Equal("CommunicationError", result.Error!.Code);
    }

    [Fact]
    public async Task Timeout_ClassifiedAsTimeout()
    {
        var handler = new StubHandler(_ => throw new TaskCanceledException("timeout"));
        var wrapper = new HttpClientWrapper(Options(maxRetries: 2), NullLogger<HttpClientWrapper>.Instance, handler);

        var result = await wrapper.SendAsync(new HttpRequest { Path = "/api/x", Method = HttpMethod.Get });

        Assert.True(result.IsFailure);
        Assert.Equal("Timeout", result.Error!.Code);
    }

    [Fact]
    public async Task IdempotentGet_RetriesOnServerError()
    {
        var handler = new StubHandler(_ => ServerError());
        var wrapper = new HttpClientWrapper(Options(maxRetries: 2), NullLogger<HttpClientWrapper>.Instance, handler);

        var result = await wrapper.SendAsync(new HttpRequest { Path = "/api/x", Method = HttpMethod.Get });

        Assert.True(result.IsSuccess);
        Assert.Equal(500, result.Value!.StatusCode);
        Assert.Equal(3, handler.Calls);
    }

    [Fact]
    public async Task UploadPost_DoesNotRetry()
    {
        var handler = new StubHandler(_ => ServerError());
        var wrapper = new HttpClientWrapper(Options(maxRetries: 2), NullLogger<HttpClientWrapper>.Instance, handler);

        var result = await wrapper.UploadAsync("/api/upload", new { batchId = "b1" });

        Assert.True(result.IsFailure);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task ConsecutiveFailures_ReachFaulted_ThenSuccessRecovers()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("down"));
        var wrapper = new HttpClientWrapper(Options(maxRetries: 2), NullLogger<HttpClientWrapper>.Instance, handler);
        var states = new List<HttpConnectionState>();
        wrapper.StateChanged += s => states.Add(s);

        // MaxRetries=2 → 连续 3 次失败进入 Faulted
        for (var i = 0; i < 3; i++)
            await wrapper.SendAsync(new HttpRequest { Path = "/api/x", Method = HttpMethod.Get });

        Assert.Equal(HttpConnectionState.Faulted, wrapper.State);
        Assert.Contains(HttpConnectionState.Faulted, states);

        // 成功请求重置计数并恢复 Connected
        handler.SetRespond(_ => Ok());
        Assert.True((await wrapper.HealthCheckAsync()).IsSuccess);
        Assert.Equal(HttpConnectionState.Connected, wrapper.State);
    }

    [Fact]
    public async Task CallerCancellation_IsNotCountedAsFailure()
    {
        var handler = new StubHandler(_ => Ok());
        var wrapper = new HttpClientWrapper(Options(maxRetries: 2), NullLogger<HttpClientWrapper>.Instance, handler);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            wrapper.SendAsync(new HttpRequest { Path = "/api/x", Method = HttpMethod.Get }, cts.Token));

        Assert.Equal(HttpConnectionState.Disconnected, wrapper.State);
    }

}
