using System.Diagnostics;
using Microsoft.Extensions.Logging;
using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Events;
using NitroGateway.Domain.Measurements;
using NitroGateway.Shared;
using NitroGateway.Storage.Buffer;
using NitroGateway.Storage.Disk;
using NitroGateway.Storage.TimeSeries;
using NitroGateway.Telemetry;
using NitroGateway.Telemetry.Tracing;

namespace NitroGateway.Collection;

/// <summary>
/// 数据分发实现。双写时序库 + 转发缓冲，互不阻塞；事件通过 <see cref="SinkDispatcher"/> 的 Channel 异步推送。
/// <para><b>设计意图：</b>采集热路径只做入队与转发缓冲写入，落库与事件消费均异步化，
/// 避免存储/订阅方慢速阻塞采集循环。</para>
/// </summary>
public sealed class DataDispatcher : IDataDispatcher
{
    private readonly MeasurementWriteHost _measurement;
    private readonly IForwardBuffer _buffer;
    private readonly SinkDispatcher _sinks;
    private readonly IDiskStatus? _diskStatus;
    private readonly IReadOnlyList<string> _forwardChannels;
    private readonly string _siteId;
    private readonly ChangeDetector? _changeDetector;
    private readonly IForwardMqttToggle? _forwardMqttToggle;

    private readonly ILogger<DataDispatcher> _logger;

    public DataDispatcher(
        MeasurementWriteHost measurement,
        IForwardBuffer buffer,
        SinkDispatcher sinks,
        ILogger<DataDispatcher> logger,
        IDiskStatus? diskStatus = null,
        IReadOnlyList<string>? forwardChannels = null,
        string? siteId = null,
        ChangeDetector? changeDetector = null,
        IForwardMqttToggle? forwardMqttToggle = null)
    {
        _measurement = measurement;
        _buffer = buffer;
        _sinks = sinks;
        _diskStatus = diskStatus;
        _changeDetector = changeDetector;
        _forwardChannels = forwardChannels is { Count: > 0 }
            ? forwardChannels
            : [IForwardBuffer.MqttChannel];
        _siteId = siteId ?? "";
        _forwardMqttToggle = forwardMqttToggle;
        _logger = logger;
    }

    /// <inheritdoc />
    /// <remarks>
    /// 三个步骤相互独立：时序 Channel 满则丢弃并告警；缓冲入队失败记录日志；
    /// 事件推送非阻塞。任一步骤失败不阻断其余步骤。
    /// </remarks>
    public async Task<OperationResult> DispatchAsync(
        Guid deviceId, IReadOnlyList<PointSnapshot> snapshots, CancellationToken ct)
    {
        using var activity = GatewayActivitySource.Source.StartActivity(GatewayActivities.Dispatch);
        activity?.SetTag(GatewayActivityTags.DeviceId, deviceId.ToString());
        activity?.SetTag(GatewayActivityTags.SnapshotCount, snapshots.Count);

        if (snapshots.Count == 0)
        {
            activity?.SetStatus(ActivityStatusCode.Ok);
            return OperationResult.Success();
        }

        // 采点计数与新鲜度：在磁盘/死区分支之前统计，保证"采到的点"能覆盖后续任何丢弃，
        // 便于用 采点 = 转发成功 + 死区抑制 + 丢失 对账。
        NitroMetrics.CollectionPointsTotal.Inc(snapshots.Count);
        SetLatestSampleTimestamp(snapshots);

        // 采集循环继续运行（CPU 侧不写盘），等级恢复后数据流自动恢复。跳过不记日志（等级变化
        // 已由 DiskGuardService 记 Warning），避免热路径每轮刷屏。
        if (_diskStatus?.Level == DiskLevel.Critical)
        {
            NitroMetrics.DispatchSkippedTotal.WithLabels("disk_critical").Inc();
            activity?.SetTag(GatewayActivityTags.ErrorMessage, "disk critical, dispatch skipped");
            return OperationResult.Success();
        }

        // ADR-053 第一刀：死区变化抑制——在 Dispatcher 层统一计算一次放行子集，
        // 存储(SQLite)、转发(MQTT)、推送(SignalR) 三处共用，避免各算一遍、语义不一致。
        // 事件仍发全量（桌面实时图/告警不受影响），PersistedSnapshots 携带实际放行子集。
        var toStore = _changeDetector?.Filter(snapshots, DateTime.UtcNow) ?? snapshots;

        // 死区抑制量：采点 - 放行 = 故意不转发的点，用于与"真丢"区分
        var suppressed = snapshots.Count - toStore.Count;
        if (suppressed > 0)
            NitroMetrics.DeadbandSuppressedPointsTotal.Inc(suppressed);

        if (toStore.Count > 0)
        {
            // ── 写时序库（只写放行子集）──
            var posted = _measurement.Post(toStore);
            if (!posted)
            {
                _logger.LogWarning("Measurement Channel 已满，丢弃数据");
            }

            // ── 入转发缓冲（只转放行子集）──
            var batch = ToBatchMeasurements(deviceId, toStore);
            // 避免缓冲表以 batchId 为主键时 same Id 冲突；各通道引擎按通道隔离出队互不争抢。
            foreach (var channel in _forwardChannels)
            {
                // 语义：无缓冲堆积、不触发丢弃；恢复后从关闭时刻起续传，不补发关闭期数据。
                // 未注册开关（独立测试/旧宿主）视为恒启用。
                if (channel == IForwardBuffer.MqttChannel && _forwardMqttToggle is { IsEnabled: false })
                    continue;

                var channelBatch = _forwardChannels.Count > 1
                    ? batch with { Id = Guid.NewGuid() }
                    : batch;
                var enqueueSw = Stopwatch.StartNew();
                var bufResult = await _buffer.EnqueueAsync(channelBatch, channel, ct);
                enqueueSw.Stop();
                NitroMetrics.BufferEnqueueDurationMs.Observe(enqueueSw.Elapsed.TotalMilliseconds);
                if (bufResult.IsFailure)
                {
                    NitroMetrics.BufferEnqueueFailures.Inc();
                    var err = bufResult.Error!;
                    if (err.Severity >= OperationalSeverity.Error)
                        _logger.LogError("缓冲入队失败 [{Code}] {Message}（通道 {Channel}）", err.Code, err.Message, channel);
                    else
                        _logger.LogWarning("缓冲入队失败: {Message}（通道 {Channel}）", err.Message, channel);
                }
            }
        }

        // ── 通知订阅方（Channel 推送，非阻塞）──
        // Snapshots 永远全量；PersistedSnapshots 为放行子集（全抑制时为空列表，SignalR 据此跳过）
        _sinks.Post(new PointStoredEvent
        {
            DeviceId = deviceId,
            Snapshots = snapshots,
            PersistedSnapshots = toStore
        });


        activity?.SetStatus(ActivityStatusCode.Ok);
        return OperationResult.Success();
    }

    /// <summary>上报本批最新采样时间戳（Unix 秒）；未知 Kind 按 UTC 处理。</summary>
    private static void SetLatestSampleTimestamp(IReadOnlyList<PointSnapshot> snapshots)
    {
        var newest = snapshots.Max(s => s.Timestamp);
        var newestUtc = newest.Kind == DateTimeKind.Utc
            ? newest
            : DateTime.SpecifyKind(newest, DateTimeKind.Utc);
        NitroMetrics.LatestSampleTimestampSeconds.Set(
            new DateTimeOffset(newestUtc).ToUnixTimeMilliseconds() / 1000.0);
    }

    private BatchMeasurements ToBatchMeasurements(
        Guid deviceId, IReadOnlyList<PointSnapshot> snapshots)
    {
        var now = DateTime.UtcNow;
        // ReceivedAt 保持"网关接收到该数据的时间"语义不变。
        var timestamps = snapshots.Select(s => s.Timestamp);
        return new BatchMeasurements
        {
            Id = Guid.NewGuid(),
            DeviceId = deviceId,
            // ADR-035 第 1 步：负载携带站点标识，HTTP 等无 topic 通道据此区分站点
            SiteId = _siteId,
            ScanStartedAt = timestamps.Min(),
            ScanCompletedAt = timestamps.Max(),
            Records = snapshots.Select(s => new MeasurementRecord
            {
                Id = Guid.NewGuid(),
                DeviceId = s.DeviceId,
                DevicePointId = s.DevicePointId,
                PointName = s.PointName ?? string.Empty,
                Value = s.Value,
                // 不再恒为 Float，云端可正确解析 Bool/Int/String 点位。
                DataType = s.DataType,
                // 转发 payload 携带点位读写权限（由快照透传），云端自动注册据此识别可写点位
                Access = s.Access,
                Timestamp = s.Timestamp,
                ReceivedAt = now,
                Quality = s.Quality
            }).ToList()
        };
    }
}
