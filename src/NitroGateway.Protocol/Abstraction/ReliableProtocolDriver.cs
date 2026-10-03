using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Protocols;
using NitroGateway.Primitives.Resilience;
using NitroGateway.Shared;
using Microsoft.Extensions.Logging;
using Polly;

namespace NitroGateway.Protocol.Abstractions
{
    internal class ReliableProtocolDriver : IProtocolDriver, IBrowseableDriver, ISubscriptionSource
    {
        private const int DefaultMaxRetryAttempts = 3;
        private static readonly TimeSpan DefaultRetryInterval = TimeSpan.FromMilliseconds(500);

        /// <summary>
        /// 内部预算系数：实际用于"尝试+退避"的部分占总预算的比例，其余留给外层总超时作余量，
        /// 避免最后一次尝试或退避恰好被总寿命截断。
        /// </summary>
        private const double InternalBudgetFraction = 0.8;

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
            // 总预算（含重试与退避）：一轮读必须在此时间内结束，默认 5s（对应 RequestTimeoutMs）。
            // 旧实现把它当作"总寿命"却配指数退避 1s，退避和 7s 直接超预算，未重试完即被截断。
            var totalBudget = requestTimeout ?? TimeSpan.FromSeconds(5);
            var attempts = Math.Max(0, maxRetryAttempts ?? DefaultMaxRetryAttempts);
            var firstDelay = retryDelay ?? DefaultRetryInterval;
            _maxRetryAttempts = attempts;

            // 每次尝试超时与退避预算均从总预算内派生，保证 (尝试+退避) 之和不超过总预算，
            // 从而让 RetryCount 真正生效。仅配置了重试时派生；无重试则单次尝试吃满总预算。
            TimeSpan? attemptTimeout = null;
            if (attempts > 0)
            {
                // 只用总预算的 80%，给外层总超时留余量。
                var usableTicks = Math.Max(1, (long)(totalBudget.Ticks * InternalBudgetFraction));
                // 退避预算占内部预算 1/5，其余均分给 (重试+1) 次尝试。
                var backoffBudgetTicks = usableTicks / 5;
                var attemptTicks = Math.Max(1, (usableTicks - backoffBudgetTicks) / (attempts + 1));
                attemptTimeout = TimeSpan.FromTicks(attemptTicks);

                // 指数退避 (D,2D,4D…) 之和 = D·(2^attempts−1)，令其不超过退避预算，
                // 且不高于设备配置的 RetryIntervalMs（配置过大时按预算收敛）。
                var expSum = Math.Pow(2, attempts) - 1;
                var fitDelayMs = backoffBudgetTicks * 1000.0 / TimeSpan.TicksPerMillisecond / expSum;
                var effectiveDelayMs = Math.Max(1, Math.Min(firstDelay.TotalMilliseconds, fitDelayMs));
                firstDelay = TimeSpan.FromMilliseconds(effectiveDelayMs);
            }

            // 机制（Polly 管线构造）由通用工厂统一；此处只给策略参数（ADR-075 机制的延伸）。
            _pipeline = ResiliencePipelineFactory.Build(
                new ResiliencePolicy
                {
                    MaxRetryAttempts = attempts,                    // 重试次数；为 0 时工厂自动跳过重试策略
                    RetryDelay = firstDelay,                        // 首次重试延迟（已按总预算收敛）
                    BackoffType = DelayBackoffType.Exponential,     // 指数退避
                    Timeout = totalBudget,                          // 外层：整条管线总预算（硬上限）
                    AttemptTimeout = attemptTimeout,                // 内层：每次尝试超时（退避不计入）
                    MaxDelay = attemptTimeout,                      // 单次退避不超过一次尝试时长
                    RetryLogLevel = LogLevel.Debug,
                    OperationName = "协议读取"
                },
                _logger);
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
            if (IsDisposed)
                return OperationalError.Protocol("驱动已释放");

            if (_inner is not ISubscriptionSource source)
                return OperationalError.Protocol("协议不支持订阅采集");

            var connect = await EnsureConnectedAsync(ct);
            if (connect.IsFailure)
                return connect;

            return await source.EnsureSubscriptionAsync(points, publishingIntervalMs, ct);
        }

        /// <inheritdoc />
        public Task<OperationResult> StopSubscriptionAsync(CancellationToken ct = default)
        {
            if (IsDisposed)
                return Task.FromResult(OperationResult.Failure(OperationalError.Protocol("驱动已释放")));

            return _inner is ISubscriptionSource source
                ? source.StopSubscriptionAsync(ct)
                : Task.FromResult<OperationResult>(OperationalError.Protocol("协议不支持订阅采集"));
        }

        /// <summary>
        /// 建连：已连接直接返回，否则转调内层 <c>ConnectAsync</c>。
        /// <para>串行化由<b>各具体驱动实例自身的闸门</b>负责（ADR-074），装饰器只做超时/重试/自动建连编排。</para>
        /// </summary>
        public Task<OperationResult> ConnectAsync(CancellationToken ct = default)
            => IsDisposed
                ? Task.FromResult(OperationResult.Failure(OperationalError.Protocol("驱动已释放")))
                : EnsureConnectedAsync(ct);

        /// <summary>透传到内层驱动</summary>
        public Task<OperationResult> DisconnectAsync(CancellationToken ct = default)
            => IsDisposed
                ? Task.FromResult(OperationResult.Failure(OperationalError.Protocol("驱动已释放")))
                : _inner.DisconnectAsync(ct);

        /// <summary>透传到内层驱动</summary>
        public Task<OperationResult> PingAsync(CancellationToken ct = default)
            => IsDisposed
                ? Task.FromResult(OperationResult.Failure(OperationalError.Protocol("驱动已释放")))
                : _inner.PingAsync(ct);

        /// <summary>透传到内层驱动（不经过 Polly，由上层控制重试）</summary>
        public Task<OperationResult<RawPointValue>> ReadAsync(DevicePoint point, CancellationToken ct = default)
            => IsDisposed
                ? Task.FromResult(OperationResult<RawPointValue>.Failure(OperationalError.Protocol("驱动已释放")))
                : _inner.ReadAsync(point, ct);

        /// <summary>
        /// 批量读取 — 核心方法，经过 Polly 管线。
        /// 步骤：检查连接 → 自动建连 → 超时读取 → 失败则抛异常触发重试。
        /// 全部重试耗尽后返回 OperationResult（不抛异常），由上层 DeviceCollector 最终记 Warning。
        /// </summary>
        public async Task<OperationResult<IReadOnlyList<RawPointValue>>> ReadBatchAsync(
            IEnumerable<DevicePoint> points,
            CancellationToken ct = default)
        {
            if (IsDisposed)
                return OperationResult<IReadOnlyList<RawPointValue>>.Failure(OperationalError.Protocol("驱动已释放"));

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
            => IsDisposed
                ? Task.FromResult(OperationResult.Failure(OperationalError.Protocol("驱动已释放")))
                : _inner.WriteAsync(point, value, ct);

        /// <summary>透传到内层驱动（不经过 Polly，由上层控制重试）</summary>
        public Task<OperationResult> WriteBatchAsync(IEnumerable<KeyValuePair<DevicePoint, object>> entries, CancellationToken ct = default)
            => IsDisposed
                ? Task.FromResult(OperationResult.Failure(OperationalError.Protocol("驱动已释放")))
                : _inner.WriteBatchAsync(entries, ct);

        /// <summary>
        /// 透传节点浏览（ADR-070 层次 1）：内层驱动支持时转发，否则返回明确失败。
        /// 浏览是配置工具，不经 Polly、不自动建连（由调用方按 WriteService 同范式先连接）；
        /// 用后不断连，长连接留给采集复用。
        /// </summary>
        public Task<OperationResult<IReadOnlyList<BrowseNode>>> BrowseAsync(
            string parentNodeId = "", CancellationToken ct = default)
        {
            if (IsDisposed)
                return Task.FromResult(OperationResult<IReadOnlyList<BrowseNode>>.Failure(
                    OperationalError.Protocol("驱动已释放")));

            return _inner is IBrowseableDriver browseable
                ? browseable.BrowseAsync(parentNodeId, ct)
                : Task.FromResult<OperationResult<IReadOnlyList<BrowseNode>>>(
                    OperationalError.Protocol("协议不支持节点浏览"));
        }

        /// <summary>0=未释放，1=已释放；保证 <see cref="Dispose"/> 幂等。</summary>
        private int _disposed;

        /// <summary>装饰器是否已释放；释放后到达的调用一律快速失败、不触达内层。</summary>
        private bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        /// <summary>
        /// 释放内层驱动资源（TCP socket、底层客户端等）；幂等。
        /// <para><b>不排水（ADR-077）</b>：不等待在途调用——装饰器只做可靠性横切，
        /// 不承担并发生命周期原语。释放后到达的调用由 <see cref="IsDisposed"/> 检查快速失败；
        /// 释放瞬间已通过检查的在途调用可能失败，由调用方按可恢复错误处理（见并发模型 X1）。
        /// 若在此同步等待在途续体，在带 SynchronizationContext 的宿主（桌面 WPF）会与
        /// 在途调用的续体互等 → 死锁，故明确不做。</para>
        /// </summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _inner.Dispose();
        }

        /// <summary>
        /// 异步释放：置位后异步拆除内层（可等待内层闸门）。与同步 <see cref="Dispose"/> 共用
        /// <see cref="_disposed"/>，先到先得。与同步路径一样不做引用计数排水（ADR-077），
        /// 但异步等待不阻塞线程，故不会造成宿主 UI 死锁。
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            await _inner.DisposeAsync();
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
