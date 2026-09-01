using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Protocols;
using NitroGateway.Shared;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;
using Polly.Timeout;

namespace NitroGateway.Protocol.Abstractions
{
    /// <summary>
    /// 可靠协议驱动装饰器。
    /// 包裹具体协议驱动（Modbus / S7 / OPC UA），在 <see cref="ReadBatchAsync"/> 上叠加：
    /// <list type="bullet">
    /// <item><b>自动建连</b> — 状态非 Connected 时先调 <c>ConnectAsync</c>。</item>
    /// <item><b>Polly 重试管线</b> — <see cref="MaxRetryAttempts"/> 次重试 + 指数退避（500ms 起），
    /// 每次尝试独立超时（默认取设备连接参数 RequestTimeoutMs，ADR-019 P2-4，不再硬编码 3s）。</item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// <para><b>日志语义分层：</b></para>
    /// <para>
    /// Driver 层只打 Debug 日志（单次重试的细节，ADR-019 P2-5 降级避免离线设备刷屏）。
    /// 最终的失败 Warning 由上层 DeviceCollector 记录，因为它持有设备名等业务上下文。
    /// </para>
    /// <para>
    /// 写入操作（Write / WriteBatch）和单点读取（ReadAsync）透传到内层，不经过 Polly。
    /// </para>
    /// </remarks>
    internal class ReliableProtocolDriver : IProtocolDriver, IBrowseableDriver
    {
        /// <summary>默认最大重试次数；生产由 DeviceConnection.RetryCount 注入（ADR-030 P1）</summary>
        private const int DefaultMaxRetryAttempts = 3;
        /// <summary>默认首次重试延迟；生产由 DeviceConnection.RetryIntervalMs 注入（ADR-030 P1）</summary>
        private static readonly TimeSpan DefaultRetryInterval = TimeSpan.FromMilliseconds(500);

        private readonly IProtocolDriver _inner;
        private readonly ResiliencePipeline _pipeline;
        private readonly ILogger<ReliableProtocolDriver> _logger;
        /// <summary>配置/注入的最大重试次数，用于最终失败日志（ADR-030 P1）</summary>
        private readonly int _maxRetryAttempts;

        /// <summary>创建可靠驱动装饰器</summary>
        /// <param name="inner">具体协议驱动实例</param>
        /// <param name="logger">日志记录器</param>
        /// <param name="requestTimeout">单次尝试超时；null 时默认 5s（对应 DeviceConnection.RequestTimeoutMs 默认值）</param>
        /// <param name="maxRetryAttempts">最大重试次数；0 = 不重试；null 时默认 3（DeviceConnection.RetryCount 默认值）</param>
        /// <param name="retryDelay">首次重试延迟（指数退避起点）；null 时默认 500ms（DeviceConnection.RetryIntervalMs 由工厂注入）</param>
        public ReliableProtocolDriver(
            IProtocolDriver inner,
            ILogger<ReliableProtocolDriver> logger,
            TimeSpan? requestTimeout = null,
            int? maxRetryAttempts = null,
            TimeSpan? retryDelay = null)
        {
            _inner = inner;
            _logger = logger;
            // ADR-019 P2-4：管线超时从设备连接参数注入（默认 5s），不再硬编码 3s——
            // 原 3s 乐观超时先于设备超时（RequestTimeoutMs，默认 5s）触发，被超时的读继续持有闸门，
            // 产生与设备实际行为不符的"超时"日志并拖长重试窗口。
            var timeout = requestTimeout ?? TimeSpan.FromSeconds(5);
            var attempts = maxRetryAttempts ?? DefaultMaxRetryAttempts;
            var firstDelay = retryDelay ?? DefaultRetryInterval;
            _maxRetryAttempts = attempts;

            var builder = new ResiliencePipelineBuilder()
                .AddTimeout(timeout);                    // 每次尝试独立超时

            // Polly 要求 MaxRetryAttempts ≥ 1；为 0 时（测试用）直接跳过重试策略
            if (attempts > 0)
            {
                builder.AddRetry(new RetryStrategyOptions
                {
                    MaxRetryAttempts = attempts,
                    Delay = firstDelay,                 // 首次重试延迟
                    BackoffType = DelayBackoffType.Exponential, // 500ms → 1s → 2s
                    OnRetry = args =>
                    {
                        // ADR-019 P2-5：重试明细降 Debug（离线设备 N 台 × 每秒多行 Info 刷屏）
                        _logger.LogDebug(
                            "第 {Attempt}/{Max} 次重试（{DelayMs}ms 后）: {Error}",
                            args.AttemptNumber + 1,
                            attempts,
                            args.RetryDelay.TotalMilliseconds,
                            args.Outcome.Exception?.Message ?? "未知");
                        return ValueTask.CompletedTask;
                    }
                });
            }

            _pipeline = builder.Build();
        }

        /// <inheritdoc />
        public DriverState State => _inner.State;

        /// <inheritdoc />
        public DriverCapability Capability => _inner.Capability;

        /// <summary>透传到内层驱动</summary>
        public Task<OperationResult> ConnectAsync(CancellationToken ct = default)
            => _inner.ConnectAsync(ct);

        /// <summary>透传到内层驱动</summary>
        public Task<OperationResult> DisconnectAsync(CancellationToken ct = default)
            => _inner.DisconnectAsync(ct);

        /// <summary>透传到内层驱动</summary>
        public Task<OperationResult> PingAsync(CancellationToken ct = default)
            => _inner.PingAsync(ct);

        /// <summary>透传到内层驱动（不经过 Polly，由上层控制重试）</summary>
        public Task<OperationResult<RawPointValue>> ReadAsync(DevicePoint point, CancellationToken ct = default)
            => _inner.ReadAsync(point, ct);

        /// <summary>
        /// 批量读取 — 核心方法，经过 Polly 管线。
        /// 步骤：检查连接 → 自动建连 → 超时读取 → 失败则抛异常触发重试。
        /// 全部重试耗尽后返回 OperationResult（不抛异常），由上层 DeviceCollector 最终记 Warning。
        /// </summary>
        public async Task<OperationResult<IReadOnlyList<RawPointValue>>> ReadBatchAsync(
            IEnumerable<DevicePoint> points,
            CancellationToken ct = default)
        {
            try
            {
                return await _pipeline.ExecuteAsync(async token =>
                {
                    if (_inner.State != DriverState.Connected)
                    {
                        var connect = await _inner.ConnectAsync(token);
                        if (connect.IsFailure)
                            throw new Exception(connect.Error!.Message);
                    }

                    var result = await _inner.ReadBatchAsync(points, token);
                    if (result.IsFailure)
                        throw new Exception(result.Error!.Message);

                    return result;
                }, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 最终失败：Debug 级别，不重复 Warning
                // 上层 DeviceCollector 持有设备名，负责记录最终 Warning
                _logger.LogDebug("通信失败（已重试 {RetryCount} 次）: {Error}", _maxRetryAttempts, ex.Message);
                return OperationResult<IReadOnlyList<RawPointValue>>.Failure(
                    OperationalError.Protocol(ex.Message));
            }
        }

        /// <summary>透传到内层驱动（不经过 Polly，由上层控制重试）</summary>
        public Task<OperationResult> WriteAsync(DevicePoint point, object value, CancellationToken ct = default)
            => _inner.WriteAsync(point, value, ct);

        /// <summary>透传到内层驱动（不经过 Polly，由上层控制重试）</summary>
        public Task<OperationResult> WriteBatchAsync(IEnumerable<KeyValuePair<DevicePoint, object>> entries, CancellationToken ct = default)
            => _inner.WriteBatchAsync(entries, ct);

        /// <summary>
        /// 透传节点浏览（ADR-070 层次 1）：内层驱动支持时转发，否则返回明确失败。
        /// 浏览是配置工具，不经 Polly、不自动建连（由调用方按 WriteService 同范式先连接）；
        /// 用后不断连，长连接留给采集复用。
        /// </summary>
        public Task<OperationResult<IReadOnlyList<BrowseNode>>> BrowseAsync(
            string parentNodeId = "", CancellationToken ct = default)
            => _inner is IBrowseableDriver browseable
                ? browseable.BrowseAsync(parentNodeId, ct)
                : Task.FromResult<OperationResult<IReadOnlyList<BrowseNode>>>(
                    OperationalError.Protocol("协议不支持节点浏览"));

        /// <summary>释放内层驱动资源（TCP socket、底层客户端等）</summary>
        public void Dispose() => _inner.Dispose();
    }
}
