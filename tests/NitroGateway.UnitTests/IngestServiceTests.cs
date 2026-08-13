using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Measurements;
using NitroGateway.Forwarder;
using NitroGateway.Ingest;
using NitroGateway.Persistence;
using NitroGateway.Shared;
using NitroGateway.Storage.TimeSeries;
using NitroGateway.Telemetry;
using NitroGateway.Transport.MQTT;
using Xunit;

namespace NitroGateway.UnitTests;

/// <summary>
/// 中心 Ingest 测试（ADR-025 P0 / ADR-028 P2-1）：
/// 遥测批量幂等入库（INSERT OR IGNORE + 去重计数）、告警 UPSERT 状态迁移、失败路径指标、契约订阅。
/// 使用临时文件库 + 真实迁移（M001~M007），验证中心库复用现有 schema（D3）。
/// 注意：Prometheus 计数器是进程级全局状态，指标断言一律取差值，避免测试间相互污染。
/// </summary>
public sealed class IngestServiceTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ntg-ingest-{Guid.NewGuid():N}.db");
    private readonly string _connectionString;
    private readonly FakeMqttClient _mqtt = new();

    public IngestServiceTests()
    {
        _connectionString = $"Data Source={_dbPath};Pooling=False";
        MigrationRunner.Run(_connectionString);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    [Fact]
    public async Task MeasurementsBatch_inserted_then_redelivery_is_deduped()
    {
        var service = CreateService();
        var batch = NewBatch(3, out var deviceId);
        var payload = new JsonMessageSerializer().Serialize(batch);
        var msg = new MqttMessage { Topic = $"nitrogateway/{deviceId}/measurements", Payload = payload };

        var receivedBefore = Metric(IngestService.KindMeasurements).Received;
        var dedupBefore = Metric(IngestService.KindMeasurements).Dedup;
        var failuresBefore = Metric(IngestService.KindMeasurements).Failures;

        await service.ProcessMessageAsync(msg, CancellationToken.None);

        Assert.Equal(3, CountRows("measurements"));
        Assert.Equal(receivedBefore + 1, Metric(IngestService.KindMeasurements).Received);
        Assert.Equal(dedupBefore, Metric(IngestService.KindMeasurements).Dedup);

        // 至少一次语义：同一批次重复投递 → 主键冲突全被忽略，行数不变、去重计数 +3
        await service.ProcessMessageAsync(msg, CancellationToken.None);

        Assert.Equal(3, CountRows("measurements"));
        Assert.Equal(dedupBefore + 3, Metric(IngestService.KindMeasurements).Dedup);
        Assert.Equal(failuresBefore, Metric(IngestService.KindMeasurements).Failures);
    }

    [Fact]
    public async Task Overlapping_batches_only_insert_new_records()
    {
        var service = CreateService();
        var (deviceId, pointId) = (Guid.NewGuid(), Guid.NewGuid());
        var r1 = NewRecord(deviceId, pointId, "p1", 10);
        var r2 = NewRecord(deviceId, Guid.NewGuid(), "p2", 20);
        var r3 = NewRecord(deviceId, Guid.NewGuid(), "p3", 30);

        var dedupBefore = Metric(IngestService.KindMeasurements).Dedup;

        await service.ProcessMessageAsync(Msg(deviceId, new BatchMeasurements { Id = Guid.NewGuid(), DeviceId = deviceId, Records = [r1, r2] }), CancellationToken.None);
        await service.ProcessMessageAsync(Msg(deviceId, new BatchMeasurements { Id = Guid.NewGuid(), DeviceId = deviceId, Records = [r1, r3] }), CancellationToken.None);

        Assert.Equal(3, CountRows("measurements"));
        Assert.Equal(dedupBefore + 1, Metric(IngestService.KindMeasurements).Dedup);
    }

    /// <summary>ADR-025 P1：payload 顶层 v 版本字段——新载荷输出 v=1 正常入库；旧载荷（无 v）按 v1 兼容读取</summary>
    [Fact]
    public async Task Payload_version_field_is_emitted_and_legacy_payload_accepted()
    {
        var service = CreateService();
        var batch = NewBatch(1, out var deviceId);

        // 新载荷：JsonMessageSerializer 在顶层输出 v=1
        var jsonWithV = System.Text.Encoding.UTF8.GetString(new JsonMessageSerializer().Serialize(batch));
        Assert.Contains("\"v\":1", jsonWithV);

        var receivedBefore = Metric(IngestService.KindMeasurements).Received;
        var failuresBefore = Metric(IngestService.KindMeasurements).Failures;

        await service.ProcessMessageAsync(Msg(deviceId, batch), CancellationToken.None);
        Assert.Equal(1, CountRows("measurements"));

        // 旧载荷：匿名对象序列化（顶层无 v）——反序列化 V=0 → 按 v1 兼容读取，正常入库
        var legacyRecord = NewRecord(deviceId, Guid.NewGuid(), "legacy-p1", 42.0);
        var legacyPayload = JsonSerializer.Serialize(new
        {
            id = Guid.NewGuid(),
            deviceId,
            scanStartedAt = DateTime.UtcNow.AddSeconds(-1),
            scanCompletedAt = DateTime.UtcNow,
            records = new[] { legacyRecord }
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        Assert.DoesNotContain("\"v\":", legacyPayload);

        var legacyMsg = new MqttMessage
        {
            Topic = $"nitrogateway/{deviceId}/measurements",
            Payload = System.Text.Encoding.UTF8.GetBytes(legacyPayload)
        };
        await service.ProcessMessageAsync(legacyMsg, CancellationToken.None);

        Assert.Equal(2, CountRows("measurements"));
        Assert.Equal(receivedBefore + 2, Metric(IngestService.KindMeasurements).Received);
        Assert.Equal(failuresBefore, Metric(IngestService.KindMeasurements).Failures);
    }

    [Fact]
    public async Task Malformed_payload_is_dropped_with_failure_metric()
    {
        var service = CreateService();
        var msg = new MqttMessage { Topic = "nitrogateway/dev1/measurements", Payload = "{not-json"u8.ToArray() };

        var receivedBefore = Metric(IngestService.KindMeasurements).Received;
        var failuresBefore = Metric(IngestService.KindMeasurements).Failures;

        await service.ProcessMessageAsync(msg, CancellationToken.None);

        Assert.Equal(0, CountRows("measurements"));
        Assert.Equal(failuresBefore + 1, Metric(IngestService.KindMeasurements).Failures);
        Assert.Equal(receivedBefore, Metric(IngestService.KindMeasurements).Received);
    }

    [Fact]
    public async Task Alarm_upsert_tracks_lifecycle_state_transitions()
    {
        var service = CreateService();
        var alarmId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();

        var receivedBefore = Metric(IngestService.KindAlarms).Received;
        var failuresBefore = Metric(IngestService.KindAlarms).Failures;

        await service.ProcessMessageAsync(AlarmMsg(alarmId, deviceId, "Active"), CancellationToken.None);
        Assert.Equal("Active", GetAlarmState(alarmId));
        Assert.Equal(receivedBefore + 1, Metric(IngestService.KindAlarms).Received);

        // 同一告警状态迁移 → 仍单行，state 被覆盖（UPSERT 而非新增）
        await service.ProcessMessageAsync(AlarmMsg(alarmId, deviceId, "Resolved"), CancellationToken.None);

        Assert.Equal(1, CountRows("alarms"));
        Assert.Equal("Resolved", GetAlarmState(alarmId));
        Assert.Equal(receivedBefore + 2, Metric(IngestService.KindAlarms).Received);
        Assert.Equal(failuresBefore, Metric(IngestService.KindAlarms).Failures);
    }

    [Fact]
    public async Task ExecuteAsync_subscribes_to_contract_topics()
    {
        var service = CreateService();
        using var cts = new CancellationTokenSource();
        var run = service.StartAsync(cts.Token);

        await WaitUntilAsync(() =>
            _mqtt.SubscribedTopics.Contains(IngestService.MeasurementsTopicFilter)
            && _mqtt.SubscribedTopics.Contains(IngestService.AlarmsTopicFilter));

        cts.Cancel();
        await run;
    }

    [Fact]
    public async Task Write_failures_retry_then_report_failure_metric()
    {
        var failuresBefore = Metric(IngestService.KindMeasurements).Failures;

        // 前 1 次失败 → 第 2 次成功：重试生效，最终入库成功、无 failure 计数
        var flaky = new FlakyStore(failuresBeforeSuccess: 1, new SqliteIngestStore(_connectionString));
        var service = CreateService(flaky);
        var batch = NewBatch(1, out var deviceId);
        var msg = new MqttMessage { Topic = $"nitrogateway/{deviceId}/measurements", Payload = new JsonMessageSerializer().Serialize(batch) };

        await service.ProcessMessageAsync(msg, CancellationToken.None);

        Assert.Equal(2, flaky.Attempts);
        Assert.Equal(1, CountRows("measurements"));
        Assert.Equal(failuresBefore, Metric(IngestService.KindMeasurements).Failures);

        // 永远失败 → 3 次尝试后丢弃 + failure 计数 +1
        var alwaysFail = new FlakyStore(failuresBeforeSuccess: int.MaxValue, new SqliteIngestStore(_connectionString));
        var service2 = CreateService(alwaysFail);

        await service2.ProcessMessageAsync(msg, CancellationToken.None);

        Assert.Equal(3, alwaysFail.Attempts);
        Assert.Equal(failuresBefore + 1, Metric(IngestService.KindMeasurements).Failures);
    }

    [Fact]
    public async Task ProcessMessage_registers_site_with_client_id()
    {
        var catalog = new FakeSiteCatalog([]);
        var service = CreateService(catalog: catalog);
        var batch = NewBatch(1, out var deviceId);
        var msg = new MqttMessage
        {
            Topic = $"nitrogateway/site-abc123/{deviceId}/measurements",
            Payload = new JsonMessageSerializer().Serialize(batch),
            ClientId = "NitroGateway-PC01-abc12345"
        };

        await service.ProcessMessageAsync(msg, CancellationToken.None);

        Assert.Equal(1, catalog.RegisterCalls);
        Assert.Equal("site-abc123", catalog.LastSiteId);
        Assert.Equal("NitroGateway-PC01-abc12345", catalog.LastClientId);
    }

    // ═══════════ 工具 ═══════════

    private IngestService CreateService(IIngestStore? store = null, ISiteCatalog? catalog = null)
        => new(_mqtt, store ?? new SqliteIngestStore(_connectionString),
            catalog ?? new FakeSiteCatalog([]),
            NullLogger<IngestService>.Instance, retryBaseDelay: TimeSpan.Zero);

    /// <summary>指标快照（差值断言用）</summary>
    private static (double Received, double Dedup, double Failures) Metric(string kind) => (
        NitroMetrics.IngestReceivedTotal.WithLabels(kind).Value,
        NitroMetrics.IngestDedupTotal.WithLabels(kind).Value,
        NitroMetrics.IngestFailuresTotal.WithLabels(kind).Value);

    private static MqttMessage Msg(Guid deviceId, BatchMeasurements batch)
        => new() { Topic = $"nitrogateway/{deviceId}/measurements", Payload = new JsonMessageSerializer().Serialize(batch) };

    private static MqttMessage AlarmMsg(Guid alarmId, Guid deviceId, string state)
    {
        var payload = JsonSerializer.Serialize(new
        {
            alarmId,
            ruleId = Guid.NewGuid(),
            deviceId,
            pointId = Guid.NewGuid(),
            triggerValue = 100.5,
            threshold = 100.0,
            severity = "Warning",
            message = "温度超限",
            state,
            occurredAt = DateTime.UtcNow
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        return new MqttMessage { Topic = $"nitrogateway/{deviceId}/alarms", Payload = System.Text.Encoding.UTF8.GetBytes(payload) };
    }

    private static BatchMeasurements NewBatch(int count, out Guid deviceId)
    {
        deviceId = Guid.NewGuid();
        var did = deviceId; // out 参数不能进 lambda，先拷贝到本地
        return new BatchMeasurements
        {
            Id = Guid.NewGuid(),
            DeviceId = deviceId,
            ScanStartedAt = DateTime.UtcNow.AddSeconds(-1),
            ScanCompletedAt = DateTime.UtcNow,
            Records = Enumerable.Range(1, count)
                .Select(i => NewRecord(did, Guid.NewGuid(), $"p{i}", i * 1.5))
                .ToList()
        };
    }

    private static MeasurementRecord NewRecord(Guid deviceId, Guid pointId, string name, double value) => new()
    {
        Id = Guid.NewGuid(),
        DeviceId = deviceId,
        DevicePointId = pointId,
        PointName = name,
        Value = value,
        DataType = DataType.Double,
        Timestamp = DateTime.UtcNow,
        ReceivedAt = DateTime.UtcNow,
        Quality = QualityCode.Good
    };

    private int CountRows(string table)
    {
        using var conn = new SqliteConnection(_connectionString);
        return conn.ExecuteScalar<int>($"SELECT COUNT(*) FROM {table}");
    }

    private string? GetAlarmState(Guid alarmId)
    {
        using var conn = new SqliteConnection(_connectionString);
        return conn.ExecuteScalar<string>("SELECT state FROM alarms WHERE id = @id", new { id = alarmId.ToString("D") });
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(20);
        }
        throw new TimeoutException("等待条件超时");
    }

    /// <summary>可注入失败次数的入库替身：前 N 次调用失败，之后委托真实实现（验证 D5 重试）</summary>
    private sealed class FlakyStore : IIngestStore
    {
        private readonly int _failuresBeforeSuccess;
        private readonly IIngestStore _inner;

        public int Attempts { get; private set; }

        public FlakyStore(int failuresBeforeSuccess, IIngestStore inner)
        {
            _failuresBeforeSuccess = failuresBeforeSuccess;
            _inner = inner;
        }

        public async Task<OperationResult<IngestWriteResult>> WriteMeasurementsAsync(
            IReadOnlyList<MeasurementRecord> records, CancellationToken ct = default)
        {
            Attempts++;
            if (Attempts <= _failuresBeforeSuccess)
                return NitroGateway.Shared.OperationalError.General("模拟写入失败");
            return await _inner.WriteMeasurementsAsync(records, ct);
        }

        public Task<OperationResult> UpsertAlarmAsync(IngestAlarmMessage alarm, CancellationToken ct = default)
            => _inner.UpsertAlarmAsync(alarm, ct);
    }
}
