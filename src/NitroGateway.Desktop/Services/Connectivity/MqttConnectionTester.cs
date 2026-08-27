using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NitroGateway.Transport.MQTT;

namespace NitroGateway.Desktop.Services.Connectivity;

/// <summary>
/// MQTT Broker 连接测试实现（ADR-067）：Connect + 发布测试消息双验，参照设备连接测试
/// （ADR-023 防假阳性）——只连通不代表可用，发布成功才确认「可写入」。
/// 构造 <see cref="MqttClientWrapper"/> 独立临时实例（无状态监听者），与运行中转发连接完全隔离；
/// 不重连（<see cref="MqttConnectionOptions.MaxReconnectAttempts"/> 置 0），避免失败拖长等待。
/// </summary>
public sealed class MqttConnectionTester : IMqttConnectionTester
{
    /// <summary>
    /// 测试连接整体超时：broker 不可达/端口未开时避免无限等待（Connect 默认无超时）。
    /// 内部可写以便单测缩短（默认 8s）。
    /// </summary>
    internal static TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(8);

    private readonly ILogger<MqttConnectionTester> _logger;
    private readonly string _siteId;
    private readonly Func<MqttConnectionOptions, IMqttClient> _clientFactory;

    /// <param name="configuration">宿主配置（读 Site:Id 拼测试 topic）</param>
    /// <param name="logger">日志记录器</param>
    /// <param name="mqttLogger">MQTT 客户端日志记录器（默认客户端工厂使用）</param>
    /// <param name="clientFactory">客户端工厂（测试注入替身；缺省 new 独立 MqttClientWrapper）</param>
    public MqttConnectionTester(
        IConfiguration configuration,
        ILogger<MqttConnectionTester> logger,
        ILogger<MqttClientWrapper> mqttLogger,
        Func<MqttConnectionOptions, IMqttClient>? clientFactory = null)
    {
        _logger = logger;
        _siteId = configuration["Site:Id"] ?? "default";
        // 独立临时客户端：无状态监听者（不推 UI/遥测），不挂转发开关（恒启用），与运行中连接隔离
        _clientFactory = clientFactory
            ?? (options => new MqttClientWrapper(options, mqttLogger, Array.Empty<IMqttStateListener>()));
    }

    /// <inheritdoc />
    public async Task<MqttConnectionTestResult> TestAsync(
        string host, int port, bool useTls, string? username, string? password, CancellationToken ct = default)
    {
        // 超时保护：与调用方令牌链式合并，任一触发即取消（Stopwatch 计时 + 8s 上限）
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(Timeout);
        var token = timeoutCts.Token;

        var options = new MqttConnectionOptions
        {
            Host = host.Trim(),
            Port = port,
            UseTls = useTls,
            Username = string.IsNullOrWhiteSpace(username) ? null : username.Trim(),
            Password = password,
            MaxReconnectAttempts = 0 // 测试连接不重连，失败直接返回
        };

        var client = _clientFactory(options);
        try
        {
            var sw = Stopwatch.StartNew();
            var connect = await client.ConnectAsync(token);
            if (!connect.IsSuccess)
            {
                sw.Stop();
                _logger.LogDebug("MQTT 测试连接失败: {Error}", connect.Error?.Message);
                return new MqttConnectionTestResult(false, sw.ElapsedMilliseconds, connect.Error?.Message ?? "连接失败");
            }

            // 发布测试消息验证 broker 接受写入（QoS1；NoMatchingSubscribers 按成功处理，不验证订阅端）
            var payload = Encoding.UTF8.GetBytes("{\"kind\":\"connection-test\"}");
            var publish = await client.PublishAsync(BuildTestTopic(), payload, qos: 1, token);
            sw.Stop();

            if (publish.IsFailure)
            {
                _logger.LogDebug("MQTT 测试消息发布失败: {Error}", publish.Error?.Message);
                return new MqttConnectionTestResult(false, sw.ElapsedMilliseconds, publish.Error?.Message ?? "发布测试消息失败");
            }

            _logger.LogInformation("MQTT 测试连接成功: {Host}:{Port}（{Elapsed}ms）", host, port, sw.ElapsedMilliseconds);
            return new MqttConnectionTestResult(true, sw.ElapsedMilliseconds, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // 内部 8s 超时（非调用方取消）：broker 不可达典型症状
            return new MqttConnectionTestResult(false, 0, $"连接超时（>{Timeout.TotalSeconds:0}s）：请检查地址/端口/防火墙");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "MQTT 测试连接异常");
            return new MqttConnectionTestResult(false, 0, ex.Message);
        }
        finally
        {
            try { await client.DisconnectAsync(CancellationToken.None); } catch { /* 断开失败不影响测试结果 */ }
            if (client is IAsyncDisposable disposable)
            {
                try { await disposable.DisposeAsync(); } catch { /* 释放失败不影响测试结果 */ }
            }
        }
    }

    /// <summary>测试消息 topic：对齐 Forwarder 上行模板 <c>nitrogateway/{siteId}/...</c> 首层/站点层。</summary>
    private string BuildTestTopic() => $"nitrogateway/{_siteId}/connection-test";
}
