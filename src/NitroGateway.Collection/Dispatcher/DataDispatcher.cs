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

/// <summary>数据分发实现。双写时序库 + 转发缓冲，互不阻塞。事件通过 SinkDispatcher Channel 异步推送。</summary>
public sealed class DataDispatcher : IDataDispatcher
{
    private readonly MeasurementWriteHost _measurement;
    private readonly IForwardBuffer _buffer;
    private readonly SinkDispatcher _sinks;

    private readonly ILogger<DataDispatcher> _logger;

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

    private static BatchMeasurements ToBatchMeasurements(
        Guid deviceId, IReadOnlyList<PointSnapshot> snapshots)
    {
        var now = DateTime.UtcNow;
        return new BatchMeasurements
        {
            Id = Guid.NewGuid(),
            DeviceId = deviceId,
            ScanStartedAt = now,
            ScanCompletedAt = now,
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
