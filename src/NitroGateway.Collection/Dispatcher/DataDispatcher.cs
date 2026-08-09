using System.Diagnostics;
using Microsoft.Extensions.Logging;
using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Events;
using NitroGateway.Domain.Measurements;
using NitroGateway.Shared;
using NitroGateway.Storage.Buffer;
using NitroGateway.Storage.TimeSeries;
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

    private readonly ILogger<DataDispatcher> _logger;

    /// <summary>创建数据分发器。</summary>
    /// <param name="measurement">时序写入宿主；通过有界 Channel 异步落库</param>
    /// <param name="buffer">转发缓冲；MQTT 转发消费的数据源</param>
    /// <param name="sinks">事件分发器；负责把存储事件推送给各 IPointStoredSink</param>
    /// <param name="logger">日志记录器</param>
    public DataDispatcher(
        MeasurementWriteHost measurement,
        IForwardBuffer buffer,
        SinkDispatcher sinks,
        ILogger<DataDispatcher> logger)
    {
        _measurement = measurement;
        _buffer = buffer;
        _sinks = sinks;
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
        // ── 写时序库 ──
        var posted=_measurement.Post(snapshots);
        if (!posted)
        {
            _logger.LogWarning("Measurement Channel 已满，丢弃数据");
        }

        // ── 入转发缓冲 ──
        var batch = ToBatchMeasurements(deviceId, snapshots);
        var bufResult = await _buffer.EnqueueAsync(batch, ct);
        if (bufResult.IsFailure)
        {
            var err = bufResult.Error!;
            if (err.Severity >= OperationalSeverity.Error)
                _logger.LogError("缓冲入队失败 [{Code}] {Message}", err.Code, err.Message);
            else
                _logger.LogWarning("缓冲入队失败: {Message}", err.Message);
        }

        // ── 通知订阅方（Channel 推送，非阻塞）──
        _sinks.Post(new PointStoredEvent { DeviceId = deviceId, Snapshots = snapshots });


        activity?.SetStatus(ActivityStatusCode.Ok);
        return OperationResult.Success();
    }

    /// <summary>
    /// 将点位快照转换为转发批次（<see cref="BatchMeasurements"/>）。
    /// 转发 payload 携带点位真实类型（<see cref="PointSnapshot.DataType"/> 透传，ADR-001 P1-5），
    /// 云端据此解析 Bool/Int/String 点位，而非恒按 Float。
    /// </summary>
    /// <param name="deviceId">所属设备 ID</param>
    /// <param name="snapshots">点位快照列表</param>
    private static BatchMeasurements ToBatchMeasurements(
        Guid deviceId, IReadOnlyList<PointSnapshot> snapshots)
    {
        var now = DateTime.UtcNow;
        // ADR-016 P3-4：批次扫描窗口取快照时间戳 min/max（此前恒为分发时刻，元数据失真）；
        // ReceivedAt 保持"网关接收到该数据的时间"语义不变。
        var timestamps = snapshots.Select(s => s.Timestamp);
        return new BatchMeasurements
        {
            Id = Guid.NewGuid(),
            DeviceId = deviceId,
            ScanStartedAt = timestamps.Min(),
            ScanCompletedAt = timestamps.Max(),
            Records = snapshots.Select(s => new MeasurementRecord
            {
                Id = Guid.NewGuid(),
                DeviceId = s.DeviceId,
                DevicePointId = s.DevicePointId,
                PointName = s.PointName ?? string.Empty,
                Value = s.Value,
                // ADR-001 P1-5：转发 payload 携带点位真实类型（由快照透传），
                // 不再恒为 Float，云端可正确解析 Bool/Int/String 点位。
                DataType = s.DataType,
                Timestamp = s.Timestamp,
                ReceivedAt = now,
                Quality = s.Quality
            }).ToList()
        };
    }
}
