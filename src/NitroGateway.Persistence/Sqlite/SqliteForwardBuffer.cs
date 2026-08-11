using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using NitroGateway.Domain.Measurements;
using NitroGateway.Shared;
using NitroGateway.Storage.Buffer;
using NitroGateway.Telemetry;

namespace NitroGateway.Persistence.Sqlite;

/// <summary>
/// SQLite 转发缓冲实现。FIFO 队列，两阶段提交，带死信队列。
/// ADR-001 P1-4：每个操作使用独立 SqliteConnection（参考 SqliteMeasurementStore 模式），
/// 不再共享 Singleton 连接，避免 Collection/Forwarder/Alarm 跨线程并发使用同一连接。
/// 状态机：Pending（待转发）→ InFlight（已出队，转发中）→ 提交删除 / 失败重试回 Pending /
/// 超过重试上限进 DeadLetter；启动时遗留 InFlight 自动重置为 Pending（延迟到首次使用，ADR-018 P3-5）。
/// 入队有上限防护（ADR-018 P2-3），死信支持按保留期自动清理。
/// </summary>
public sealed class SqliteForwardBuffer : IForwardBuffer
{
    /// <summary>死信清理单批删除行数上限（每批独立事务，批间让出写锁窗口）</summary>
    private const int DefaultPurgeBatchSize = 10_000;

    /// <summary>入队上限默认值：MQTT 长期离线时防止 Pending 无限累积拖垮磁盘/查询</summary>
    private const int DefaultMaxPending = 100_000;

    private readonly string _connectionString;
    private readonly int _maxRetries;
    private readonly int _maxPending;
    private readonly ILogger<SqliteForwardBuffer> _logger;
    private readonly SemaphoreSlim _recoveryGate = new(1, 1);
    private readonly JsonSerializerOptions _json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>启动恢复是否已完成（volatile 读取，避免每操作都进闸门）</summary>
    private bool _recoveryCompleted;

    /// <summary>
    /// 待转发批次数（不含死信）。同步查询，仅保留接口兼容（接口只增不删）；
    /// async 路径请用 <see cref="GetCountAsync"/>，避免同步阻塞（ADR-001 P3-13）。
    /// </summary>
    public int Count
    {
        get
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            SqlitePragmas.Apply(conn);
            return conn.ExecuteScalar<int>("SELECT COUNT(*) FROM forward_buffer WHERE status = 'Pending'");
        }
    }

    /// <summary>
    /// 异步获取待转发批次数（不含死信）。ADR-001 P3-13：async 路径不再同步 ExecuteScalar。
    /// ADR-017 P1-1：与其余方法一致，DB 瞬时故障不抛出——记 Warning 并按 0 处理，
    /// 避免 BackgroundService 因单次计数查询故障整机退出（StopHost）；调用方依赖
    /// "不抛异常"契约（ForwarderEngine 积压检查 / 停机排空 / StatusController）。
    /// 取消仍抛出（OCE），由调用方按停机路径处理。
    /// </summary>
    public async Task<int> GetCountAsync(CancellationToken ct = default)
    {
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            SqlitePragmas.Apply(conn);
            return await conn.ExecuteScalarAsync<int>(
                new CommandDefinition(
                    "SELECT COUNT(*) FROM forward_buffer WHERE status = 'Pending'",
                    cancellationToken: ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var error = SqliteErrorClassifier.Classify(ex, "Buffer 积压计数失败");
            _logger.LogWarning("{Context}，按 0 处理: {Error}", "Buffer 积压计数失败", error.Message);
            return 0;
        }
    }

    /// <summary>
    /// 构造缓冲。启动恢复（InFlight → Pending）不再在构造器内同步执行（ADR-018 P3-5）：
    /// 原实现在 DI 首次解析时同步打开连接，DB 锁/不可用时（busy_timeout 5s）阻塞首解析；
    /// 改为首次被使用时经 <see cref="EnsureRecoveredAsync"/> 异步完成，恢复完成前其余操作等待同一闸门，
    /// 保证顺序正确。恢复失败仅告警不阻断（下次操作仍会重试）。
    /// </summary>
    /// <param name="connectionString">SQLite 连接串</param>
    /// <param name="logger">日志记录器</param>
    /// <param name="maxRetries">最大重试次数，超过后移入死信队列。默认 5</param>
    /// <param name="maxPending">Pending 入队上限，达到后拒绝入队（ADR-018 P2-3）。默认 100000</param>
    public SqliteForwardBuffer(
        string connectionString,
        ILogger<SqliteForwardBuffer> logger,
        int maxRetries = 5,
        int maxPending = DefaultMaxPending)
    {
        _connectionString = connectionString;
        _logger = logger;
        _maxRetries = maxRetries;
        _maxPending = Math.Max(1, maxPending);
    }

    /// <summary>
    /// 确保启动恢复已完成：把上次进程异常退出遗留的 InFlight 批次全部重置为 Pending，
    /// 避免批次永久卡死造成静默丢数（P0-1①）。恢复失败仅告警，不阻断操作（下次仍会重试）。
    /// </summary>
    private async Task EnsureRecoveredAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref _recoveryCompleted)) return;

        await _recoveryGate.WaitAsync(ct);
        try
        {
            if (Volatile.Read(ref _recoveryCompleted)) return;
            try
            {
                await using var conn = new SqliteConnection(_connectionString);
                await conn.OpenAsync(ct);
                SqlitePragmas.Apply(conn);
                var recovered = await conn.ExecuteAsync(
                    "UPDATE forward_buffer SET status = 'Pending' WHERE status = 'InFlight'");
                if (recovered > 0)
                {
                    _logger.LogWarning(
                        "启动恢复：{Count} 个 InFlight 转发批次已重置为 Pending（上次进程可能异常退出）",
                        recovered);
                }
                Volatile.Write(ref _recoveryCompleted, true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "启动恢复 InFlight 批次失败");
            }
        }
        finally
        {
            _recoveryGate.Release();
        }
    }

    /// <summary>
    /// 入队一批待转发数据：序列化为 CamelCase JSON 后以 Pending 状态插入（retry_count=0）。
    /// 达到入队上限（<see cref="_maxPending"/>）时拒绝入队并返回 Storage 失败（ADR-018 P2-3），
    /// 由 DataDispatcher 记 Error 告警，避免 MQTT 长期离线时 Pending 无限累积。
    /// 单条 INSERT，异常归类返回，不抛出（保证 DataDispatcher 的失败降级分支可达）。
    /// </summary>
    public async Task<OperationResult> EnqueueAsync(BatchMeasurements batch, CancellationToken ct = default)
        => await EnqueueAsync(batch, IForwardBuffer.MqttChannel, ct);

    /// <summary>
    /// 入队到指定通道（ADR-011）：channel 列随行写入，出队按通道隔离。
    /// 其余语义与 <see cref="EnqueueAsync(BatchMeasurements, CancellationToken)"/> 一致。
    /// </summary>
    public async Task<OperationResult> EnqueueAsync(BatchMeasurements batch, string channel, CancellationToken ct = default)
    {
        // P0-2：入队异常统一走 SqliteErrorClassifier，与 Dequeue/Commit/MarkFailed 一致，
        // 使 DataDispatcher 的优雅降级分支（bufResult.IsFailure）真正可达。
        try
        {
            await EnsureRecoveredAsync(ct);

            var payload = JsonSerializer.Serialize(batch, _json);
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            SqlitePragmas.Apply(conn);

            // ADR-018 P2-3：入队上限防护（COUNT + INSERT 非原子，并发下可能略超上限，best-effort 足够）
            var pending = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM forward_buffer WHERE status = 'Pending'");
            if (pending >= _maxPending)
            {
                _logger.LogError("转发缓冲已满（上限 {Max}），拒绝入队 {BatchId}", _maxPending, batch.Id);
                return OperationalError.Storage($"转发缓冲已满（上限 {_maxPending}），拒绝入队");
            }

            await conn.ExecuteAsync(
                "INSERT INTO forward_buffer (id, payload, status, retry_count, enqueued_at, channel) VALUES (@id, @payload, 'Pending', 0, @ts, @channel)",
                new { id = batch.Id.ToString(), payload, ts = DateTime.UtcNow.ToString("O"), channel });
            return OperationResult.Success();
        }
        catch (Exception ex)
        {
            return SqliteErrorClassifier.Classify(ex, "Buffer 入队失败");
        }
    }

    /// <summary>
    /// 出队最多 maxCount 批 Pending 数据（FIFO，按 enqueued_at 升序）。
    /// 两阶段提交：同一事务内 SELECT + UPDATE 标记 InFlight，随后事务外反序列化负载；
    /// 反序列化失败的行经 <see cref="RecoverCorruptRowAsync"/> 恢复（重试计数+1，超限进死信），
    /// 不影响其余行出队。空队返回空列表。异常归类返回，不抛出。
    /// </summary>
    public async Task<OperationResult<IReadOnlyList<BatchMeasurements>>> DequeueAsync(
    int maxCount,
    CancellationToken ct = default)
        => await DequeueAsync(maxCount, IForwardBuffer.MqttChannel, ct);

    /// <summary>
    /// 出队指定通道最多 maxCount 批 Pending 数据（ADR-011）：FIFO 按 (channel, enqueued_at) 升序。
    /// 其余语义与 <see cref="DequeueAsync(int, CancellationToken)"/> 一致。
    /// </summary>
    public async Task<OperationResult<IReadOnlyList<BatchMeasurements>>> DequeueAsync(
    int maxCount,
    string channel,
    CancellationToken ct = default)
    {
        await EnsureRecoveredAsync(ct);

        List<BufferRow> rows;
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            SqlitePragmas.Apply(conn);
            // ① 查询待发送的数据并标记为 InFlight（同一事务两阶段提交）
            await using var tx = await conn.BeginTransactionAsync(ct);

            rows = (await conn.QueryAsync<BufferRow>(
                new CommandDefinition(@"SELECT id, payload FROM forward_buffer WHERE status = 'Pending' AND channel = @channel
                  ORDER BY enqueued_at ASC LIMIT @max",
                    new { max = maxCount, channel },
                    transaction: tx,
                    cancellationToken: ct)))
                .ToList();

            if (rows.Count == 0)
            {
                await tx.CommitAsync(ct);
                return Array.Empty<BatchMeasurements>();
            }

            await conn.ExecuteAsync(
                new CommandDefinition( @"UPDATE forward_buffer SET status = 'InFlight' 
                    WHERE id IN @ids",
                    new
                    {
                        ids = rows.Select(r => r.Id)
                    },
                    transaction: tx,
                    cancellationToken: ct));

            await tx.CommitAsync(ct);
        }
        catch (Exception ex)
        {
            // 事务未提交时在作用域结束时自动回滚
            return SqliteErrorClassifier.Classify(ex, "Buffer 出队失败");
        }

        // ② 反序列化。损坏行不能卡在 InFlight（P0-1②）：
        //    重置为 Pending + retry_count+1 + last_error，超过 _maxRetries 进死信。
        //    事务已随作用域释放，恢复逻辑可复用 MarkFailedAsync 开启新事务。
        var result = new List<BatchMeasurements>(rows.Count);
        foreach (var row in rows)
        {
            BatchMeasurements? batch;
            try
            {
                batch = JsonSerializer.Deserialize<BatchMeasurements>(row.Payload, _json);
            }
            catch (Exception ex)
            {
                await RecoverCorruptRowAsync(row.Id, $"反序列化失败: {ex.Message}", ct);
                continue;
            }

            if (batch is null)
            {
                await RecoverCorruptRowAsync(row.Id, "反序列化结果为 null（负载损坏）", ct);
                continue;
            }

            result.Add(batch);
        }

        return result;
    }

    /// <summary>
    /// P0-1② 出队反序列化失败恢复：复用 <see cref="MarkFailedAsync"/> 的重试/死信逻辑，
    /// 恢复自身失败仅记日志，不影响其余行出队。
    /// </summary>
    private async Task RecoverCorruptRowAsync(string id, string reason, CancellationToken ct)
    {
        try
        {
            var result = await MarkFailedAsync(Guid.Parse(id), reason, ct);
            if (result.IsFailure)
            {
                _logger.LogError("恢复损坏批次 {BatchId} 失败: {Error}", id, result.Error!.Message);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "恢复损坏批次 {BatchId} 异常", id);
        }
    }

    /// <summary>
    /// 确认转发成功：单事务内按 ID 批量删除已出队批次（InFlight → 移除）。
    /// ADR-018 P3-1：仅删除处于 InFlight 的行——若该行已被 MarkFailed 重置为 Pending
    /// （未实际发送）而 stale commit 到达，不能把未发送数据删掉；非 InFlight 视为已提交/已失败，跳过。
    /// 空列表直接成功；异常归类返回，不抛出。
    /// </summary>
    public async Task<OperationResult> CommitAsync(IReadOnlyList<Guid> batchIds, CancellationToken ct = default)
    {
        if (batchIds.Count == 0) return OperationResult.Success();

        await EnsureRecoveredAsync(ct);

        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            SqlitePragmas.Apply(conn);
            await using var tx = await conn.BeginTransactionAsync(ct);
            await conn.ExecuteAsync(
                "DELETE FROM forward_buffer WHERE id IN @ids AND status = 'InFlight'",
                new { ids = batchIds.Select(id => id.ToString()) }, tx);
            await tx.CommitAsync(ct);
            return OperationResult.Success();
        }
        catch (Exception ex)
        {
            return SqliteErrorClassifier.Classify(ex, "Buffer 提交失败");
        }
    }

    /// <summary>
    /// 标记一次转发失败：单事务内一次 UPDATE 完成重试计数+1 与状态迁移
    /// （retry_count+1 ≥ maxRetries 进 DeadLetter，否则回 Pending），并记录 last_error；
    /// 事务外再查询一次以判断是否进死信（仅供 Warning 日志）。异常归类返回，不抛出。
    /// </summary>
    public async Task<OperationResult> MarkFailedAsync(
    Guid batchId,
    string reason,
    CancellationToken ct = default)
    {
        await EnsureRecoveredAsync(ct);

        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            SqlitePragmas.Apply(conn);
            await using var tx = await conn.BeginTransactionAsync(ct);

            // ADR-001 P2-11：合并为一次 UPDATE（重试计数 + 超限进死信），3 次往返 → 2 次
            await conn.ExecuteAsync(
                @"UPDATE forward_buffer
              SET status = CASE WHEN retry_count + 1 >= @max THEN 'DeadLetter' ELSE 'Pending' END,
                    retry_count = retry_count + 1,
                    last_error = @error
              WHERE id = @id",
                new { id = batchId.ToString(), error = reason, max = _maxRetries }, tx);

            await tx.CommitAsync(ct);

            // 判断是否进入死信（供 Warning 日志）
            var deadCount = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM forward_buffer WHERE id=@id AND status='DeadLetter'",
                new { id = batchId.ToString() });
            if (deadCount > 0)
            {
                // ADR-009 P2-1：ForwardTotal 的 deadletter 标签此前无上报点（Forwarder 无法感知是否进死信，
                // 转换发生在 MarkFailed 内部，故在此上报）；与 Forwarder.cs 的 success/failure 上报互补。
                NitroMetrics.ForwardTotal.WithLabels("deadletter").Inc();
                _logger.LogWarning(
                    "转发批次 {BatchId} 进入死信队列（重试 {MaxRetries} 次后失败）: {Error}",
                    batchId, _maxRetries, reason);
            }

            return OperationResult.Success();
        }
        catch (Exception ex)
        {
            return SqliteErrorClassifier.Classify(ex, "标记失败异常");
        }
    }

    /// <summary>
    /// 获取死信队列条目（按入队时间升序，最多 maxCount 条）。
    /// 从 payload 反序列化 BatchMeasurements 提取设备/记录数，损坏负载按空批次展示（DeviceId=Empty、RecordCount=0）。
    /// 异常归类返回，不抛出。
    /// </summary>
    public async Task<OperationResult<IReadOnlyList<DeadLetterEntry>>> GetDeadLettersAsync(int maxCount, CancellationToken ct = default)
    {
        // ADR-002 P1-1/P3-5：死信查询异常统一分类，ct 传给 Dapper（与 Enqueue/Dequeue 一致）
        try
        {
            await EnsureRecoveredAsync(ct);

            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            SqlitePragmas.Apply(conn);
            var rows = await conn.QueryAsync(
                new CommandDefinition(
                    "SELECT id, payload, retry_count, last_error, enqueued_at FROM forward_buffer WHERE status = 'DeadLetter' ORDER BY enqueued_at ASC LIMIT @max",
                    new { max = maxCount },
                    cancellationToken: ct));

            return rows.Select(r =>
            {
                var batch = JsonSerializer.Deserialize<BatchMeasurements>((string)r.payload, _json);
                return new DeadLetterEntry
                {
                    BatchId = Guid.Parse((string)r.id),
                    DeviceId = batch?.DeviceId ?? Guid.Empty,
                    RecordCount = batch?.Records.Count ?? 0,
                    RetryCount = (int)r.retry_count,
                    LastError = r.last_error as string,
                    EnqueuedAt = DateTime.Parse((string)r.enqueued_at)
                };
            }).ToList();
        }
        catch (Exception ex)
        {
            return SqliteErrorClassifier.Classify(ex, "Buffer 死信查询失败");
        }
    }

    /// <summary>
    /// 死信重试：仅当条目处于 DeadLetter 时重置为 Pending（retry_count=0、last_error 清空）。
    /// 条目不存在或不在死信状态返回 NotFound Failure；异常归类返回，不抛出。
    /// </summary>
    public async Task<OperationResult> RetryDeadLetterAsync(Guid batchId, CancellationToken ct = default)
    {
        // ADR-002 P1-1/P3-5：死信重试异常统一分类，ct 传给 Dapper
        try
        {
            await EnsureRecoveredAsync(ct);

            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            SqlitePragmas.Apply(conn);
            var rows = await conn.ExecuteAsync(
                new CommandDefinition(
                    "UPDATE forward_buffer SET status = 'Pending', retry_count = 0, last_error = NULL WHERE id = @id AND status = 'DeadLetter'",
                    new { id = batchId.ToString() },
                    cancellationToken: ct));

            return rows > 0
                ? OperationResult.Success()
                : OperationalError.NotFound($"死信 {batchId} 不存在");
        }
        catch (Exception ex)
        {
            return SqliteErrorClassifier.Classify(ex, "Buffer 死信重试失败");
        }
    }

    /// <summary>
    /// 丢弃死信（物理删除）：仅当条目处于 DeadLetter 时删除。
    /// 条目不存在或不在死信状态返回 NotFound Failure；异常归类返回，不抛出。
    /// </summary>
    public async Task<OperationResult> DiscardDeadLetterAsync(Guid batchId, CancellationToken ct = default)
    {
        // ADR-002 P1-1/P3-5：死信丢弃异常统一分类，ct 传给 Dapper
        try
        {
            await EnsureRecoveredAsync(ct);

            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            SqlitePragmas.Apply(conn);
            var rows = await conn.ExecuteAsync(
                new CommandDefinition(
                    "DELETE FROM forward_buffer WHERE id = @id AND status = 'DeadLetter'",
                    new { id = batchId.ToString() },
                    cancellationToken: ct));

            return rows > 0
                ? OperationResult.Success()
                : OperationalError.NotFound($"死信 {batchId} 不存在");
        }
        catch (Exception ex)
        {
            return SqliteErrorClassifier.Classify(ex, "Buffer 死信丢弃失败");
        }
    }

    /// <summary>
    /// ADR-018 P2-3：按入队时间清理过期死信（物理删除），与 measurements 保留清理对称，
    /// 防止坏消息持续累积死信表。分批删除（单批 ≤ <see cref="DefaultPurgeBatchSize"/> 行，
    /// 每批独立事务）避免大 DELETE 长时间持锁；本 SQLite 编译版不支持 DELETE ... LIMIT，
    /// 用 SELECT id 限批 → 按 id 批量删除 实现分批。异常归类返回，不抛出。
    /// </summary>
    public async Task<OperationResult> PurgeDeadLettersAsync(DateTime before, CancellationToken ct = default)
    {
        try
        {
            await EnsureRecoveredAsync(ct);

            var cutoff = before.ToUniversalTime().ToString("O");
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                await using var conn = new SqliteConnection(_connectionString);
                await conn.OpenAsync(ct);
                SqlitePragmas.Apply(conn);
                await using var tx = await conn.BeginTransactionAsync(ct);

                var ids = (await conn.QueryAsync<string>(
                    "SELECT id FROM forward_buffer WHERE status = 'DeadLetter' AND enqueued_at < @before LIMIT @batch",
                    new { before = cutoff, batch = DefaultPurgeBatchSize }, tx)).ToList();
                if (ids.Count == 0) break;

                await conn.ExecuteAsync(
                    "DELETE FROM forward_buffer WHERE id IN @ids",
                    new { ids }, tx);
                await tx.CommitAsync(ct);

                // 本批未删满说明已清空目标行，退出循环
                if (ids.Count < DefaultPurgeBatchSize) break;
            }
            return OperationResult.Success();
        }
        catch (Exception ex)
        {
            return SqliteErrorClassifier.Classify(ex, "Buffer 死信清理失败");
        }
    }
}
