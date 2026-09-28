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
/// 基于 SQLite 的持久化转发缓冲（outbox）实现：为 Forwarder 提供断电不丢的 FIFO 队列。
/// <para>
/// 数据流：Collection 入队 → Forwarder 出队 → 成功 <see cref="CommitAsync"/> 删除 / 失败 <see cref="MarkFailedAsync"/> 重试。
/// 普通遥测旧值不值钱：重试超过上限直接丢弃（简化 2026-08-22），不再产生死信。
/// </para>
/// <c>DeadLetter</c> 为遗留状态：已不再产生，仅保留接口方法以满足 <see cref="IForwardBuffer"/>，历史行在启动恢复时清理。
/// <para>
/// 并发契约：
/// 所有公开方法（<see cref="Count"/> 属性除外）先经 <see cref="EnsureRecoveredAsync"/> 保证启动恢复完成，
/// 且各自使用独立短连接/事务；cancel 令牌触发时抛 <see cref="OperationCanceledException"/>，
/// 其余 DB 异常一律经 <see cref="SqliteErrorClassifier"/> 归类为 <see cref="OperationResult"/> 返回，不向上抛。
/// </para>
/// </summary>
public sealed class SqliteForwardOutbox : IForwardBuffer, IDisposable
{
    /// <summary>死信清理单批删除行数上限（每批独立事务，批间让出写锁窗口，避免长事务锁库）</summary>
    private const int DefaultPurgeBatchSize = 10_000;

    /// <summary>入队上限默认值：MQTT 长期离线时防止 Pending 无限累积拖垮磁盘/查询</summary>
    private const int DefaultMaxPending = 100_000;

    /// <summary>SQLite 连接串；每次操作新建短连接，由连接池复用</summary>
    private readonly string _connectionString;

    /// <summary>单批转发失败重试上限；达到即丢弃（retry_count + 1 &gt;= maxRetries）</summary>
    private readonly int _maxRetries;

    /// <summary>Pending 积压上限；<see cref="EnqueueAsync"/> 达上限即拒绝入队</summary>
    private readonly int _maxPending;

    private readonly ILogger<SqliteForwardOutbox> _logger;

    /// <summary>启动恢复闸门：确保多线程首用时 <see cref="EnsureRecoveredAsync"/> 只执行一次</summary>
    private readonly SemaphoreSlim _recoveryGate = new(1, 1);

    /// <summary>批次负载序列化选项（CamelCase，与写入/读取对称）</summary>
    private readonly JsonSerializerOptions _json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>启动恢复是否已完成（volatile 读取，避免每操作都进闸门）</summary>
    private bool _recoveryCompleted;

    /// <summary>
    /// 同步获取 Pending 批次数（不含 InFlight/DeadLetter）。
    /// 注意：不触发启动恢复、不归类异常，异常直接上抛；async 路径请用 <see cref="GetCountAsync"/>。
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
    /// <summary>构造转发缓冲。</summary>
    /// <param name="connectionString">SQLite 连接串（单例注册，进程内共享）</param>
    /// <param name="logger">日志</param>
    /// <param name="maxRetries">失败重试上限（达到即丢弃）</param>
    /// <param name="maxPending">Pending 积压上限；非法值（&lt;1）收敛为 1</param>
    public SqliteForwardOutbox(
        string connectionString,
        ILogger<SqliteForwardOutbox> logger,
        int maxRetries = 5,
        int maxPending = DefaultMaxPending)
    {
        _connectionString = connectionString;
        _logger = logger;
        _maxRetries = maxRetries;
        _maxPending = Math.Max(1, maxPending);
    }

    /// <summary>释放启动恢复闸门（Singleton，宿主关闭时调用）。</summary>
    public void Dispose() => _recoveryGate.Dispose();

    /// <summary>
    /// 打开连接并应用 PRAGMA（WAL/synchronous/busy_timeout）。
    /// 每个操作使用独立短连接；WAL 为库级持久设置，<see cref="SqlitePragmas"/> 内部已缓存跳过重复切换。
    /// </summary>
    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken ct)
    {
        var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        SqlitePragmas.Apply(conn);
        return conn;
    }

    public async Task<int> GetCountAsync(CancellationToken ct = default)
    {
        try
        {
            // 计数反映恢复后状态：首读即触发启动恢复（InFlight→Pending、清理历史死信），
            await EnsureRecoveredAsync(ct);
            await using var conn = await OpenConnectionAsync(ct);
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
                await using var conn = await OpenConnectionAsync(ct);
                var recovered = await conn.ExecuteAsync(
                    "UPDATE forward_buffer SET status = 'Pending' WHERE status = 'InFlight'");
                if (recovered > 0)
                {
                    _logger.LogWarning(
                        "启动恢复：{Count} 个 InFlight 转发批次已重置为 Pending（上次进程可能异常退出）",
                        recovered);
                }

                // 简化（2026-08-22）：旧版死信（重试超限转 DeadLetter）已无 UI/保留任务管理，且属失效遥测——
                // 启动时一次性清理，避免孤儿行永久滞留（此后 MarkFailed 超限直接 DELETE，不再产生死信）。
                var purged = await conn.ExecuteAsync(
                    "DELETE FROM forward_buffer WHERE status = 'DeadLetter'");
                if (purged > 0)
                {
                    _logger.LogWarning(
                        "启动清理：删除 {Count} 条旧版死信（已改为重试超限即丢弃策略）", purged);
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

    /// <summary>入队一批数据到默认 MQTT 通道。</summary>
    public async Task<OperationResult> EnqueueAsync(BatchMeasurements batch, CancellationToken ct = default)
        => await EnqueueAsync(batch, IForwardBuffer.MqttChannel, ct);

    /// <summary>
    /// 入队一批数据到指定通道：先确保启动恢复完成，再序列化负载，
    /// 检查 Pending 积压是否达上限（达上限拒绝），最后 INSERT 为 Pending。
    /// 入队异常统一经 <see cref="SqliteErrorClassifier"/> 归类返回，使调用方（DataDispatcher）的优雅降级分支可达。
    /// DB 异常归类返回，取消抛 OCE。
    /// </summary>
    public async Task<OperationResult> EnqueueAsync(BatchMeasurements batch, string channel, CancellationToken ct = default)
    {
        // P0-2：入队异常统一走 SqliteErrorClassifier，与 Dequeue/Commit/MarkFailed 一致，
        // 使 DataDispatcher 的优雅降级分支（bufResult.IsFailure）真正可达。
        try
        {
            await EnsureRecoveredAsync(ct);

            // 负载持久化为 CamelCase JSON（与 Dequeue 反序列化对称）
            var payload = JsonSerializer.Serialize(batch, _json);
            await using var conn = await OpenConnectionAsync(ct);

            // 背压：Pending 达上限时拒绝入队，防止长期离线导致无限累积
            var pending = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM forward_buffer WHERE status = 'Pending'");
            if (pending >= _maxPending)
            {
                _logger.LogError("转发缓冲已满（上限 {Max}），拒绝入队 {BatchId}", _maxPending, batch.Id);
                return OperationalError.Storage($"转发缓冲已满（上限 {_maxPending}），拒绝入队");
            }

            // 写入即 Pending，retry_count 从 0 起；enqueued_at 用 ISO-8601 字符串支撑 FIFO 排序
            await conn.ExecuteAsync(
                "INSERT INTO forward_buffer (id, payload, status, retry_count, enqueued_at, channel) VALUES (@id, @payload, 'Pending', 0, @ts, @channel)",
                new { id = batch.Id.ToString(), payload, ts = DateTime.UtcNow.ToString("O"), channel });
            return OperationResult.Success();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return SqliteErrorClassifier.Classify(ex, "Buffer 入队失败");
        }
    }

    /// <summary>出队最多 maxCount 批数据（默认 MQTT 通道）。</summary>
    public async Task<OperationResult<IReadOnlyList<BatchMeasurements>>> DequeueAsync(
    int maxCount,
    CancellationToken ct = default)
        => await DequeueAsync(maxCount, IForwardBuffer.MqttChannel, ct);

    /// <summary>
    /// 从指定通道出队最多 maxCount 批 Pending 数据（FIFO，按 enqueued_at 升序）。
    /// 两阶段提交：同一事务内 SELECT + UPDATE 标记 InFlight（占用行），随后事务外反序列化负载；
    /// 反序列化失败的行经 <see cref="RecoverCorruptRowAsync"/> 恢复（重试计数+1，超限即丢弃），
    /// 不影响其余行出队。空队返回空列表。DB 异常归类返回，取消抛 OCE（契约见类注释）。
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
            await using var conn = await OpenConnectionAsync(ct);
            // ① 事务内两阶段：先按通道 FIFO 取出待发送行，再统一置为 InFlight 占位，
            //    使并发 Dequeue 不会再取到同一批（只投影 id+payload，见 BufferRow）。
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
                // 空队列：无写入，提交空事务并返回空列表
                await tx.CommitAsync(ct);
                return new List<BatchMeasurements>();
            }

            // 仅在事务内标记，提交后这些行才对其他执行流"不可见"
            await conn.ExecuteAsync(
                new CommandDefinition(@"UPDATE forward_buffer SET status = 'InFlight' 
                    WHERE id IN @ids",
                    new
                    {
                        ids = rows.Select(r => r.Id)
                    },
                    transaction: tx,
                    cancellationToken: ct));

            await tx.CommitAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 事务未提交时在作用域结束时自动回滚
            return SqliteErrorClassifier.Classify(ex, "Buffer 出队失败");
        }

        // ② 反序列化。损坏行不能卡在 InFlight（P0-1②）：
        //    重置为 Pending + retry_count+1 + last_error，超过 _maxRetries 丢弃。
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
    /// P0-1② 出队反序列化失败恢复：复用 <see cref="MarkFailedAsync"/> 的重试/丢弃逻辑，
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
    /// 确认转发成功，物理删除已出队的批次。
    /// 仅删除处于 InFlight 的行（防止并发下误删已恢复/重试的行）；空列表直接成功。
    /// DB 异常归类返回，取消抛 OCE（契约见类注释）。
    /// </summary>
    public async Task<OperationResult> CommitAsync(IReadOnlyList<Guid> batchIds, CancellationToken ct = default)
    {
        // 无待提交批次，幂等短路
        if (batchIds.Count == 0) return OperationResult.Success();

        await EnsureRecoveredAsync(ct);

        try
        {
            await using var conn = await OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            await conn.ExecuteAsync(
                "DELETE FROM forward_buffer WHERE id IN @ids AND status = 'InFlight'",
                new { ids = batchIds.Select(id => id.ToString()) }, tx);
            await tx.CommitAsync(ct);
            return OperationResult.Success();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return SqliteErrorClassifier.Classify(ex, "Buffer 提交失败");
        }
    }

    /// <summary>
    /// 标记一次转发失败：单事务内重试计数+1；超限（retry_count+1 ≥ maxRetries）直接 DELETE 丢弃
    /// （普通遥测旧值不值钱，简化 2026-08-22），否则回 Pending 待下轮重试并记录 last_error。
    /// DB 异常归类返回，取消抛 OCE（契约见类注释）。
    /// </summary>
    public async Task<OperationResult> MarkFailedAsync(
    Guid batchId,
    string reason,
    CancellationToken ct = default)
    {
        await EnsureRecoveredAsync(ct);

        try
        {
            await using var conn = await OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);

            // 简化（2026-08-22）：重试超限即丢弃——先按"超限"条件 DELETE，未命中再走重试计数+1 回 Pending。
            var dropped = await conn.ExecuteAsync(
                @"DELETE FROM forward_buffer WHERE id = @id AND retry_count + 1 >= @max",
                new { id = batchId.ToString(), max = _maxRetries }, tx);

            if (dropped == 0)
            {
                // 未超限必须把状态重置回 Pending 待下轮重试（否则批次卡 InFlight 不再出队，重试机制失效）
                await conn.ExecuteAsync(
                    @"UPDATE forward_buffer
                      SET status = 'Pending', retry_count = retry_count + 1, last_error = @error
                      WHERE id = @id",
                    new { id = batchId.ToString(), error = reason }, tx);
            }

            await tx.CommitAsync(ct);

            if (dropped > 0)
            {
                // 与 Forwarder.cs 的 success/failure 上报互补：丢弃发生在 MarkFailed 内部，故在此上报。
                NitroMetrics.ForwardTotal.WithLabels("dropped").Inc();
                _logger.LogWarning(
                    "转发批次 {BatchId} 重试超限（{MaxRetries} 次）已丢弃: {Error}",
                    batchId, _maxRetries, reason);
            }

            return OperationResult.Success();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return SqliteErrorClassifier.Classify(ex, "标记失败异常");
        }
    }

    /// <summary>
    /// 【停用】获取死信队列条目（按入队时间升序，最多 maxCount 条）。
    /// 死信特性已移除（2026-08-22，重试超限即丢弃），本方法仅保留以满足 <see cref="IForwardBuffer"/> 接口只增不删；
    /// 不再产生新死信，历史死信在启动恢复时清理，正常返回空列表。
    /// 从 payload 反序列化 BatchMeasurements 提取设备/记录数，损坏负载按空批次展示（DeviceId=Empty、RecordCount=0）。
    /// DB 异常归类返回，取消抛 OCE（契约见类注释）。
    /// </summary>
    public async Task<OperationResult<IReadOnlyList<DeadLetterEntry>>> GetDeadLettersAsync(int maxCount, CancellationToken ct = default)
    {
        try
        {
            await EnsureRecoveredAsync(ct);

            await using var conn = await OpenConnectionAsync(ct);
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
                    EnqueuedAt = DateTime.Parse((string)r.enqueued_at, System.Globalization.CultureInfo.InvariantCulture)
                };
            }).ToList();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return SqliteErrorClassifier.Classify(ex, "Buffer 死信查询失败");
        }
    }

    /// <summary>
    /// 【停用】死信重试：仅当条目处于 DeadLetter 时重置为 Pending（retry_count=0、last_error 清空）。
    /// 死信特性已移除（2026-08-22），保留仅因 <see cref="IForwardBuffer"/> 接口只增不删。
    /// 条目不存在或不在死信状态返回 NotFound Failure；DB 异常归类返回，取消抛 OCE（契约见类注释）。
    /// </summary>
    public async Task<OperationResult> RetryDeadLetterAsync(Guid batchId, CancellationToken ct = default)
    {
        try
        {
            await EnsureRecoveredAsync(ct);

            await using var conn = await OpenConnectionAsync(ct);
            var rows = await conn.ExecuteAsync(
                new CommandDefinition(
                    "UPDATE forward_buffer SET status = 'Pending', retry_count = 0, last_error = NULL WHERE id = @id AND status = 'DeadLetter'",
                    new { id = batchId.ToString() },
                    cancellationToken: ct));

            return rows > 0
                ? OperationResult.Success()
                : OperationalError.NotFound($"死信 {batchId} 不存在");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return SqliteErrorClassifier.Classify(ex, "Buffer 死信重试失败");
        }
    }

    /// <summary>
    /// 【停用】丢弃死信（物理删除）：仅当条目处于 DeadLetter 时删除。
    /// 死信特性已移除（2026-08-22），保留仅因 <see cref="IForwardBuffer"/> 接口只增不删。
    /// 条目不存在或不在死信状态返回 NotFound Failure；DB 异常归类返回，取消抛 OCE（契约见类注释）。
    /// </summary>
    public async Task<OperationResult> DiscardDeadLetterAsync(Guid batchId, CancellationToken ct = default)
    {
        try
        {
            await EnsureRecoveredAsync(ct);

            await using var conn = await OpenConnectionAsync(ct);
            var rows = await conn.ExecuteAsync(
                new CommandDefinition(
                    "DELETE FROM forward_buffer WHERE id = @id AND status = 'DeadLetter'",
                    new { id = batchId.ToString() },
                    cancellationToken: ct));

            return rows > 0
                ? OperationResult.Success()
                : OperationalError.NotFound($"死信 {batchId} 不存在");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return SqliteErrorClassifier.Classify(ex, "Buffer 死信丢弃失败");
        }
    }

    /// <summary>
    /// 【停用】物理清理 before 之前入队的遗留死信。死信特性已移除（2026-08-22），保留仅为接口完整。
    /// 分批（<see cref="DefaultPurgeBatchSize"/>）循环删除，每批独立事务、批间让出写锁窗口，避免长事务锁库。
    /// DB 异常归类返回，取消抛 OCE（契约见类注释）。
    /// </summary>
    /// <param name="before">清理截止时间（早于该时间的死信才会被删除）</param>
    public async Task<OperationResult> PurgeDeadLettersAsync(DateTime before, CancellationToken ct = default)
    {
        try
        {
            await EnsureRecoveredAsync(ct);

            // 统一 UTC 的 ISO-8601 字符串，与 enqueued_at 写入格式一致才能正确比较
            var cutoff = before.ToUniversalTime().ToString("O");
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                await using var conn = await OpenConnectionAsync(ct);
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
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return SqliteErrorClassifier.Classify(ex, "Buffer 死信清理失败");
        }
    }
}
