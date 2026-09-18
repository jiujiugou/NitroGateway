using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Protocols;
using NitroGateway.Shared;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;

namespace NitroGateway.Protocol.Abstractions
{
    internal class ReliableProtocolDriver : IProtocolDriver, IBrowseableDriver, ISubscriptionSource
    {
        private const int DefaultMaxRetryAttempts = 3;
        private static readonly TimeSpan DefaultRetryInterval = TimeSpan.FromMilliseconds(500);

        private readonly IProtocolDriver _inner;
        private readonly ResiliencePipeline _pipeline;
        private readonly ILogger<ReliableProtocolDriver> _logger;
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

        /// <inheritdoc />
        public event Func<IReadOnlyList<RawPointValue>, Task>? ValuesReceived
        {
            add
            {
                if (_inner is ISubscriptionSource source)
                    source.ValuesReceived += value;
            }
            remove
            {
                if (_inner is ISubscriptionSource source)
                    source.ValuesReceived -= value;
            }
        }

        /// <inheritdoc />
        public bool IsSubscriptionActive =>
            _inner is ISubscriptionSource source && source.IsSubscriptionActive;

        /// <inheritdoc />
        public async Task<OperationResult> EnsureSubscriptionAsync(
            IReadOnlyList<DevicePoint> points,
            int publishingIntervalMs,
            CancellationToken ct = default)
        {
            if (_inner is not ISubscriptionSource source)
                return OperationalError.Protocol("协议不支持订阅采集");

            var connect = await EnsureConnectedAsync(ct);
            if (connect.IsFailure)
                return connect;

            return await source.EnsureSubscriptionAsync(points, publishingIntervalMs, ct);
        }

        /// <inheritdoc />
        public Task<OperationResult> StopSubscriptionAsync(CancellationToken ct = default)
            => _inner is ISubscriptionSource source
                ? source.StopSubscriptionAsync(ct)
                : Task.FromResult<OperationResult>(OperationalError.Protocol("协议不支持订阅采集"));

        /// <summary>
        /// 建连单飞：与读/订阅的自动建连共用 <see cref="_connectGate"/>，
        /// 保证同一驱动实例任意入口（显式 Connect + 读写/订阅触发）同时只有一次 <c>ConnectAsync</c> 在途。
        /// 此前为直接透传，导致写路径的显式建连与读路径的自动建连可并发进入内层
        /// （Modbus TCP 的 ConnectAsync 自身无闸门，会并发访问同一客户端）。
        /// </summary>
        public Task<OperationResult> ConnectAsync(CancellationToken ct = default)
            => EnsureConnectedAsync(ct);

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
                    var connect = await EnsureConnectedAsync(token);
                    if (connect.IsFailure)
                        throw new ProtocolAttemptException(connect.Error!.Message);

                    var result = await _inner.ReadBatchAsync(points, token);
                    if (result.IsFailure)
                        throw new ProtocolAttemptException(result.Error!.Message);

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

        /// <summary>0=未释放，1=已释放；保证 Dispose 幂等</summary>
        private int _disposed;

        /// <summary>释放内层驱动资源（TCP socket、底层客户端等）；幂等，重复调用安全</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _inner.Dispose();
        }

        /// <summary>
        /// 已连接直接返回，否则转调内层 <c>ConnectAsync</c>。
        /// <para>串行化由<b>各具体驱动实例自身的闸门</b>负责（ADR-074）：同一实例的
        /// Connect/Disconnect/Read/Write/Ping 都过同一把闸门，内层 <c>ConnectAsync</c> 在闸门内双检
        /// <see cref="DriverState"/>，因此并发读/订阅只会真正建连一次。装饰器不再持有建连闸门，
        /// 只做超时/重试/自动建连编排。</para>
        /// </summary>
        private Task<OperationResult> EnsureConnectedAsync(CancellationToken ct)
            => _inner.State == DriverState.Connected
                ? Task.FromResult(OperationResult.Success())
                : _inner.ConnectAsync(ct);

        /// <summary>
        /// 读/建连失败的内部信号异常：仅用于触发 Polly 重试，不对外暴露
        /// （对外一律由 <see cref="ReadBatchAsync"/> 归类为 OperationResult 失败）。
        /// </summary>
        private sealed class ProtocolAttemptException : Exception
        {
            public ProtocolAttemptException(string message) : base(message) { }
        }
    }
}
