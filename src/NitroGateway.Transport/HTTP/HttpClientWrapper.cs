using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NitroGateway.Shared;
using Polly;

namespace NitroGateway.Transport.HTTP;

/// <summary>
/// <see cref="IHttpClient"/> 的实现，基于 <c>IHttpClientFactory</c> + Polly 重试。
/// </summary>
public sealed class HttpClientWrapper : IHttpClient
{
    private readonly HttpConnectionOptions _options;
    private readonly ILogger<HttpClientWrapper> _logger;
    private readonly System.Net.Http.HttpClient _inner;
    private readonly ResiliencePipeline<HttpResponseMessage> _resilience;
    private int _consecutiveFailures;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <inheritdoc />
    public HttpConnectionState State { get; private set; } = HttpConnectionState.Disconnected;

    /// <inheritdoc />
    public event Action<HttpConnectionState>? StateChanged;

    /// <summary>创建 HTTP 客户端实例</summary>
    public HttpClientWrapper(HttpConnectionOptions options, ILogger<HttpClientWrapper> logger)
    {
        _options = options;
        _logger = logger;

        _inner = new System.Net.Http.HttpClient
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
                    logger.LogWarning("HTTP 重试 {Attempt}/{Max}，等待 {Delay}ms",
                        args.AttemptNumber, options.MaxRetries, args.RetryDelay.TotalMilliseconds);
                    return default;
                }
            })
            .Build();
    }

    /// <inheritdoc />
    public async Task<OperationResult<HttpResponse>> SendAsync(HttpRequest request, CancellationToken ct = default)
    {
        try
        {
            var httpMsg = BuildHttpMessage(request);

            var response = await _resilience.ExecuteAsync(
                async token => await _inner.SendAsync(httpMsg, token), ct);

            var body = await response.Content.ReadAsStringAsync(ct);

            OnSuccess();

            return new HttpResponse
            {
                StatusCode = (int)response.StatusCode,
                Body = body
            };
        }
        catch (Exception ex)
        {
            OnFailure(ex);
            return OperationalError.Timeout($"HTTP 请求失败 [{request.Method} {request.Path}]: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<OperationResult> UploadAsync<T>(string path, T payload, CancellationToken ct = default)
    {
        try
        {
            var response = await _resilience.ExecuteAsync(
                async token => await _inner.PostAsJsonAsync(path, payload, JsonOptions, token), ct);

            OnSuccess();

            if (response.IsSuccessStatusCode)
                return OperationResult.Success();

            var body = await response.Content.ReadAsStringAsync(ct);
            return OperationalError.General($"HTTP 上传失败 [{path}]: {response.StatusCode} - {body}");
        }
        catch (Exception ex)
        {
            OnFailure(ex);
            return OperationalError.Timeout($"HTTP 上传失败 [{path}]: {ex.Message}");
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
            : result.Error!;
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
        _consecutiveFailures = 0;
        if (State != HttpConnectionState.Connected)
        {
            State = HttpConnectionState.Connected;
            _logger.LogInformation("HTTP 连接恢复");
            StateChanged?.Invoke(State);
        }
    }

    /// <summary>请求失败时累加计数，连续失败超过阈值则进入 Faulted</summary>
    private void OnFailure(Exception ex)
    {
        _consecutiveFailures++;
        _logger.LogWarning("HTTP 请求失败 ({Consecutive} 次连续): {Error}",
            _consecutiveFailures, ex.Message);

        if (_consecutiveFailures >= _options.MaxRetries + 1 && State != HttpConnectionState.Faulted)
        {
            State = HttpConnectionState.Faulted;
            _logger.LogError("HTTP 连接故障，连续失败 {Count} 次", _consecutiveFailures);
            StateChanged?.Invoke(State);
        }
    }
}
