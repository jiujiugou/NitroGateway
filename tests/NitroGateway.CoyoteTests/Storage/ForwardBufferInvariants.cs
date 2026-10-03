using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Measurements;
using NitroGateway.Persistence.Sqlite;
using NitroGateway.Shared;
using NitroGateway.Storage.Buffer;

namespace NitroGateway.CoyoteTests;

/// <summary>
/// SqliteForwardOutbox 并发不变量（对应 notes/Invariants/forward-buffer.md I1–I4）：
/// I1 出队不重复、I2 提交后不再出现、I3 InFlight 必收敛、I4 背压不超上限。
/// <para>正例跑真实 outbox（临时 SQLite 库，多执行流并发 Enqueue/Dequeue/Commit/MarkFailed）；
/// 负控跑测试内坏缓冲 <see cref="BrokenBuffer"/>（非原子出队 / 提交不动状态 / MarkFailed 不重置 / check-then-act 入队），
/// 同一断言 harness 在坏实现上必须变红。</para>
/// </summary>
internal static class ForwardBufferInvariants
{
    // ── I1：出队不重复 ──
    public static Task I1_DequeueNoDuplicate_Positive() => RunNoDuplicateDequeue(RealSubject(maxPending: 1000));

    /// <summary>负控：出队在「读取「与」标记 InFlight」之间让出 → 并发下重复取走同一批。</summary>
    public static Task I1_DequeueNoDuplicate_Negative_Racy() => RunNoDuplicateDequeue(BrokenSubject());

    // ── I2：提交后不再出现 ──
    public static Task I2_CommitRemoves_Positive() => RunCommitRemoves(RealSubject());

    /// <summary>负控：提交不改变行状态 → 物理残留，仍可被后续读取。</summary>
    public static Task I2_CommitRemoves_Negative_Noop() => RunCommitRemoves(BrokenSubject());

    // ── I3：InFlight 必收敛 ──
    public static Task I3_InFlightConverges_Positive() => RunInFlightConvergence(RealSubject(maxRetries: 3), 3);

    /// <summary>负控：MarkFailed 无操作 → 批次永远卡 InFlight。</summary>
    public static Task I3_InFlightConverges_Negative_Stuck() => RunInFlightConvergence(BrokenSubject(), 3);

    // ── I4：背压不超上限（check-then-act 必须原子）──
    public static Task I4_Backpressure_Positive() => RunBackpressure(RealSubject(maxPending: 2), 2);

    /// <summary>负控：入队「查计数 → 插入」两步非原子 → 并发下双双插入超限。</summary>
    public static Task I4_Backpressure_Negative_CheckThenAct() => RunBackpressure(BrokenSubject(maxPending: 2), 2);

    // ══════════════ 断言 harness（正/负共用） ══════════════

    private static async Task RunNoDuplicateDequeue(Subject s)
    {
        var batches = Enumerable.Range(0, 8).Select(_ => Batch()).ToList();
        foreach (var b in batches)
        {
            var r = await s.Buffer.EnqueueAsync(b);
            if (r.IsFailure) throw new InvalidOperationException($"I1: 入队失败 {r.Error!.Message}");
        }

        var results = await Task.WhenAll(Enumerable.Range(0, 4)
            .Select(_ => Task.Run(async () => (await s.Buffer.DequeueAsync(8)).Value!.Select(x => x.Id).ToList())));

        var all = results.SelectMany(x => x).ToList();
        if (all.Count != batches.Count || all.Distinct().Count() != all.Count)
            throw new InvalidOperationException(
                $"I1: 并发出队重复/丢失：取到 {all.Count} 批（期望 {batches.Count}），唯一 {all.Distinct().Count()}");
    }

    private static async Task RunCommitRemoves(Subject s)
    {
        var b = Batch();
        await s.Buffer.EnqueueAsync(b);
        var dq = await s.Buffer.DequeueAsync(10);
        if (dq.Value!.Count != 1) throw new InvalidOperationException($"I2: 应出队 1 批，实际 {dq.Value.Count}");

        await s.Buffer.CommitAsync([b.Id]);

        var residual = await s.CountAll();
        if (residual != 0)
            throw new InvalidOperationException($"I2: 提交后仍残留 {residual} 行");
        if (await s.Buffer.GetCountAsync() != 0)
            throw new InvalidOperationException("I2: 提交后 Pending 计数非 0");
        var again = await s.Buffer.DequeueAsync(10);
        if (again.Value!.Count != 0)
            throw new InvalidOperationException("I2: 提交后批次仍可出队");
    }

    private static async Task RunInFlightConvergence(Subject s, int maxRetries)
    {
        var b = Batch();
        await s.Buffer.EnqueueAsync(b);
        var dq = await s.Buffer.DequeueAsync(10);
        if (dq.Value!.Count != 1) throw new InvalidOperationException($"I3: 应出队 1 批，实际 {dq.Value.Count}");

        for (var i = 0; i < maxRetries; i++)
            await s.Buffer.MarkFailedAsync(b.Id, "boom");

        if (await s.CountInFlight() != 0)
            throw new InvalidOperationException("I3: 超限后仍残留 InFlight（批次永久卡死）");
        if (await s.CountAll() != 0)
            throw new InvalidOperationException("I3: 超限后应丢弃（DELETE），实际仍有残留行");
    }

    private static async Task RunBackpressure(Subject s, int maxPending)
    {
        await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(_ => Task.Run(async () => await s.Buffer.EnqueueAsync(Batch()))));

        var pending = await s.Buffer.GetCountAsync();
        if (pending > maxPending)
            throw new InvalidOperationException($"I4: Pending {pending} 超过上限 {maxPending}（背压失效）");
    }

    // ══════════════ 测试对象（真实 / 坏实现） ══════════════

    private sealed class Subject
    {
        public required IForwardBuffer Buffer { get; init; }
        public required Func<Task<int>> CountAll { get; init; }
        public required Func<Task<int>> CountInFlight { get; init; }
    }

    private static Subject RealSubject(int maxRetries = 5, int maxPending = 1000)
    {
        var connStr = NewDb();
        var buf = new SqliteForwardOutbox(connStr, NullLogger<SqliteForwardOutbox>.Instance, maxRetries, maxPending);
        return new Subject
        {
            Buffer = buf,
            CountAll = () => ScalarAsync(connStr, "SELECT COUNT(*) FROM forward_buffer"),
            CountInFlight = () => ScalarAsync(connStr, "SELECT COUNT(*) FROM forward_buffer WHERE status = 'InFlight'"),
        };
    }

    private static Subject BrokenSubject(int maxPending = 1000)
    {
        var buf = new BrokenBuffer(maxPending);
        return new Subject
        {
            Buffer = buf,
            CountAll = () => Task.FromResult(buf.AllCount),
            CountInFlight = () => Task.FromResult(buf.InFlightCount),
        };
    }

    private static async Task<int> ScalarAsync(string connStr, string sql)
    {
        await using var conn = new SqliteConnection(connStr);
        await conn.OpenAsync();
        return await conn.ExecuteScalarAsync<int>(sql);
    }

    private static string NewDb()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ng-coyote-{Guid.NewGuid():N}.db");
        var connStr = $"Data Source={path}";
        using var conn = new SqliteConnection(connStr);
        conn.Open();
        conn.Execute("PRAGMA journal_mode=WAL;");
        conn.Execute("PRAGMA busy_timeout=10000;");
        conn.Execute(
            "CREATE TABLE forward_buffer (" +
            "id TEXT PRIMARY KEY, payload TEXT NOT NULL, status TEXT NOT NULL DEFAULT 'Pending', " +
            "enqueued_at TEXT NOT NULL, retry_count INTEGER NOT NULL DEFAULT 0, last_error TEXT NULL, " +
            "channel TEXT NOT NULL DEFAULT 'mqtt')");
        return connStr;
    }

    private static BatchMeasurements Batch()
    {
        var deviceId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        return new BatchMeasurements
        {
            Id = Guid.NewGuid(),
            DeviceId = deviceId,
            ScanStartedAt = now,
            ScanCompletedAt = now,
            Records =
            [
                new MeasurementRecord
                {
                    Id = Guid.NewGuid(),
                    DeviceId = deviceId,
                    DevicePointId = Guid.NewGuid(),
                    PointName = "P",
                    Value = 1.0,
                    DataType = DataType.Float,
                    Timestamp = now,
                    ReceivedAt = now,
                }
            ]
        };
    }

    // ══════════════ 坏实现（negative-control 种子） ══════════════

    /// <summary>故意写坏的内存转发缓冲：集中 I1/I2/I3/I4 四类缺陷。</summary>
    private sealed class BrokenBuffer(int maxPending) : IForwardBuffer
    {
        private readonly List<Row> _rows = [];

        public int AllCount => _rows.Count;
        public int InFlightCount => _rows.Count(r => r.Status == "InFlight");
        public int Count => _rows.Count(r => r.Status == "Pending");

        public async Task<OperationResult> EnqueueAsync(BatchMeasurements batch, string channel, CancellationToken ct = default)
        {
            // 坏：查计数与插入分两步、中间让出 → 并发双双通过上限检查
            var pending = _rows.Count(r => r.Status == "Pending");
            await Task.Yield();
            if (pending >= maxPending)
                return OperationResult.Success();
            _rows.Add(new Row(batch.Id, "Pending", channel));
            return OperationResult.Success();
        }

        public async Task<OperationResult<IReadOnlyList<BatchMeasurements>>> DequeueAsync(
            int maxCount, string channel, CancellationToken ct = default)
        {
            // 坏：读取与标记 InFlight 非原子 → 并发重复取走
            var take = _rows.Where(r => r.Status == "Pending" && r.Channel == channel).Take(maxCount).ToList();
            await Task.Yield();
            foreach (var r in take) r.Status = "InFlight";
            return OperationResult<IReadOnlyList<BatchMeasurements>>.Success(
                take.Select(r => Batch()).ToList());
        }

        // 坏：什么都不做，遗留 InFlight / 已提交行
        public Task<OperationResult> CommitAsync(IReadOnlyList<Guid> batchIds, CancellationToken ct = default)
            => Task.FromResult(OperationResult.Success());

        public Task<OperationResult> MarkFailedAsync(Guid batchId, string reason, CancellationToken ct = default)
            => Task.FromResult(OperationResult.Success());

        public Task<int> GetCountAsync(CancellationToken ct = default) => Task.FromResult(Count);
        public Task<OperationResult> EnqueueAsync(BatchMeasurements batch, CancellationToken ct = default)
            => EnqueueAsync(batch, IForwardBuffer.MqttChannel, ct);
        public Task<OperationResult<IReadOnlyList<BatchMeasurements>>> DequeueAsync(int maxCount, CancellationToken ct = default)
            => DequeueAsync(maxCount, IForwardBuffer.MqttChannel, ct);
        public Task<OperationResult<IReadOnlyList<DeadLetterEntry>>> GetDeadLettersAsync(int maxCount, CancellationToken ct = default)
            => Task.FromResult(OperationResult<IReadOnlyList<DeadLetterEntry>>.Success(Array.Empty<DeadLetterEntry>()));
        public Task<OperationResult> RetryDeadLetterAsync(Guid batchId, CancellationToken ct = default)
            => Task.FromResult(OperationResult.Success());
        public Task<OperationResult> DiscardDeadLetterAsync(Guid batchId, CancellationToken ct = default)
            => Task.FromResult(OperationResult.Success());
        public Task<OperationResult> PurgeDeadLettersAsync(DateTime before, CancellationToken ct = default)
            => Task.FromResult(OperationResult.Success());

        private sealed class Row(Guid id, string status, string channel)
        {
            public Guid Id { get; } = id;
            public string Status { get; set; } = status;
            public string Channel { get; } = channel;
        }
    }
}
