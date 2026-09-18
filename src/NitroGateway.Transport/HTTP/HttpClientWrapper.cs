using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NitroGateway.Shared;
using Polly;

namespace NitroGateway.Transport.HTTP;

public sealed class HttpClientWrapper : IHttpClient, IDisposable
{
    private readonly HttpConnectionOptions _options;
    private readonly ILogger<HttpClientWrapper> _logger;
    private readonly System.Net.Http.HttpClient _inner;
    private readonly ResiliencePipeline<HttpResponseMessage> _resilience;
    private readonly ResiliencePipeline<HttpResponseMessage> _noRetry;

    private readonly object _stateLock = new();
    private int _consecutiveFailures;
    private HttpConnectionState _state = HttpConnectionState.Disconnected;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <inheritdoc />
    public HttpConnectionState State
    {
        get { lock (_stateLock) return _state; }
    }

    /// <inheritdoc />
    public event Action<HttpConnectionState>? StateChanged;

    /// <summary>创建 HTTP 客户端实例</summary>
    public HttpClientWrapper(HttpConnectionOptions options, ILogger<HttpClientWrapper> logger)
        : this(options, logger, new HttpClientHandler())
    {
    }

    /// <summary>
    /// 测试用构造函数：允许注入 <see cref="HttpMessageHandler"/> 替身（NitroGateway.IntegrationTests 专用，
    /// 模拟成功/超时/5xx/断网等，无需真实 HTTP 服务）。
    /// </summary>
    internal HttpClientWrapper(HttpConnectionOptions options, ILogger<HttpClientWrapper> logger, HttpMessageHandler handler)
    {
        _options = options;
        _logger = logger;

        _inner = new System.Net.Http.HttpClient(handler)
        {
            BaseAddress = new Uri(options.BaseUrl.TrimEnd('/')),
            Timeout = TimeSpan.FromMilliseconds(options.TimeoutMs)
        };

        if (options.AuthType == HttpAuthType.BearerToken && !string.IsNullOrEmpty(options.BearerToken))
            _inner.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", options.BearerToken);

        _resilience = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRetry(new Polly.Retry.RetryStrategyOptions<HttpResponseMessage>
            {
                MaxRetryAttempts = options.MaxRetries,
                BackoffType = DelayBackoffType.Exponential,
                Delay = TimeSpan.FromMilliseconds(options.RetryBackoffBaseMs),
                ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                    .Handle<HttpRequestException>()
                    .Handle<TaskCanceledException>()
                    .HandleResult(r => (int)r.StatusCode >= 500),
                OnRetry = args =>
                {
                    _logger.LogWarning("HTTP 重试 {Attempt}/{Max}，等待 {Delay}ms",
                        args.AttemptNumber, options.MaxRetries, args.RetryDelay.TotalMilliseconds);
                    return default;
                }
            })
            .Build();

        _noRetry = new ResiliencePipelineBuilder<HttpResponseMessage>().Build();
    }

    /// <inheritdoc />
    public async Task<OperationResult<HttpResponse>> SendAsync(HttpRequest request, CancellationToken ct = default)
    {
        try
        {
            var pipeline = IsIdempotent(request.Method) ? _resilience : _noRetry;
            var response = await pipeline.ExecuteAsync(
                async token =>
                {
                    // "request message was already sent"（HttpClient 不允许重复发送同一消息）
                    using var httpMsg = BuildHttpMessage(request);
                    return await _inner.SendAsync(httpMsg, token);
                }, ct);

            var body = await response.Content.ReadAsStringAsync(ct);

            OnSuccess();

            return new HttpResponse
            {
                StatusCode = (int)response.StatusCode,
                Body = body
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            OnFailure(ex);
            return OperationResult<HttpResponse>.Failure(
                ClassifyError(ex, $"HTTP 请求失败 [{request.Method} {request.Path}]"));
        }
    }

    /// <inheritdoc />
    public async Task<OperationResult> UploadAsync<T>(string path, T payload, CancellationToken ct = default)
    {
        try
        {
            var response = await _noRetry.ExecuteAsync(
                async token => await _inner.PostAsJsonAsync(path, payload, JsonOptions, token), ct);

            OnSuccess();

            if (response.IsSuccessStatusCode)
                return OperationResult.Success();

            var body = await response.Content.ReadAsStringAsync(ct);
            return OperationalError.General($"HTTP 上传失败 [{path}]: {response.StatusCode} - {body}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            OnFailure(ex);
            return ClassifyError(ex, $"HTTP 上传失败 [{path}]");
        }
    }

    /// <inheritdoc />
    public async Task<OperationResult> HealthCheckAsync(CancellationToken ct = default)
    {
        var healthPath = _options.HealthPath ?? "/health";
        var request = new HttpRequest
        {
            Path = healthPath,
            Method = HttpMethod.Get
        };

        var result = await SendAsync(request, ct);
        return result.IsSuccess
            ? OperationResult.Success()
            : OperationResult.Failure(result.Error!);
    }

    // ---- 内部 ----

    /// <summary>将内部请求模型转换为 HttpRequestMessage</summary>
    private static HttpRequestMessage BuildHttpMessage(HttpRequest request)
    {
        var msg = new HttpRequestMessage(request.Method, request.Path);

        if (request.Body is not null)
            msg.Content = new StringContent(request.Body, Encoding.UTF8, "application/json");

        foreach (var (key, value) in request.Headers)
            msg.Headers.TryAddWithoutValidation(key, value);

        return msg;
    }

    /// <summary>请求成功时重置连续失败计数并恢复 Connected 状态</summary>
    private void OnSuccess()
    {
        Interlocked.Exchange(ref _consecutiveFailures, 0);

        HttpConnectionState old;
        lock (_stateLock)
        {
            old = _state;
            if (old != HttpConnectionState.Connected)
                _state = HttpConnectionState.Connected;
        }

        if (old != HttpConnectionState.Connected)
        {
            _logger.LogInformation("HTTP 连接恢复");
            StateChanged?.Invoke(HttpConnectionState.Connected);
        }
    }

    /// <summary>请求失败时累加计数，连续失败超过阈值则进入 Faulted</summary>
    private void OnFailure(Exception ex)
    {
        var failures = Interlocked.Increment(ref _consecutiveFailures);
        _logger.LogWarning("HTTP 请求失败 ({Consecutive} 次连续): {Error}",
            failures, ex.Message);

        if (failures >= _options.MaxRetries + 1)
        {
            HttpConnectionState old;
            lock (_stateLock)
            {
                old = _state;
                if (old != HttpConnectionState.Faulted)
                    _state = HttpConnectionState.Faulted;
            }

            if (old != HttpConnectionState.Faulted)
            {
                _logger.LogError("HTTP 连接故障，连续失败 {Count} 次", failures);
                StateChanged?.Invoke(HttpConnectionState.Faulted);
            }
        }
    }

    private static OperationalError ClassifyError(Exception ex, string context)
    {
        return ex switch
        {
            TaskCanceledException => OperationalError.Timeout($"{context}: {ex.Message}"),
            HttpRequestException => OperationalError.Communication($"{context}: {ex.Message}"),
            _ => OperationalError.General($"{context}: {ex.Message}")
        };
    }

    /// <summary>幂等方法（RFC 7231）允许重试；POST 等非幂等方法不重试</summary>
    private static bool IsIdempotent(HttpMethod method) =>
        method.Method.ToUpperInvariant() is "GET" or "PUT" or "DELETE" or "HEAD" or "OPTIONS" or "TRACE";

    /// <summary>释放内部 HttpClient 及其连接池（Singleton，宿主关闭时调用）。</summary>
    public void Dispose() => _inner.Dispose();
}
