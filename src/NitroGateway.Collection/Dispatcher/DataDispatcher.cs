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

/// <summary>数据分发实现。双写时序库 + 转发缓冲，互不阻塞</summary>
public sealed class DataDispatcher : IDataDispatcher
{
    private readonly IMeasurementStore _timeSeries;
    private readonly IForwardBuffer _buffer;
    private readonly IEnumerable<IPointStoredSink> _sinks;
    private readonly ILogger<DataDispatcher> _logger;

    public DataDispatcher(
        IMeasurementStore timeSeries,
        IForwardBuffer buffer,
        IEnumerable<IPointStoredSink> sinks,
        ILogger<DataDispatcher> logger)
    {
        _timeSeries = timeSeries;
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

        var tsOk = false;
        var bufOk = false;

        // ── 写时序库 ──
        var tsResult = await _timeSeries.WriteAsync(snapshots, ct);
        if (tsResult.IsSuccess)
        {
            tsOk = true;
        }
        else
        {
            var err = tsResult.Error!;
            if (err.Severity >= OperationalSeverity.Error)
                _logger.LogError("时序写入失败 [{Code}] {Message}", err.Code, err.Message);
            else
                _logger.LogWarning("时序写入失败: {Message}", err.Message);
        }

        // ── 入转发缓冲 ──
        var batch = ToBatchMeasurements(deviceId, snapshots);
        var bufResult = await _buffer.EnqueueAsync(batch, ct);
        if (bufResult.IsSuccess)
        {
            bufOk = true;
        }
        else
        {
            var err = bufResult.Error!;
            if (err.Severity >= OperationalSeverity.Error)
                _logger.LogError("缓冲入队失败 [{Code}] {Message}", err.Code, err.Message);
            else
                _logger.LogWarning("缓冲入队失败: {Message}", err.Message);
        }

        // ── 通知订阅方（不阻塞主流程）──
        _ = NotifySinksAsync(deviceId, snapshots, ct);

        if (tsOk && bufOk)
        {
            activity?.SetStatus(ActivityStatusCode.Ok);
            return OperationResult.Success();
        }

        // 取两个错误中严重性更高的作为返回值
        var worst = !tsOk && !bufOk
            ? (tsResult.Error!.Severity >= bufResult.Error!.Severity ? tsResult.Error : bufResult.Error)
            : !tsOk ? tsResult.Error! : bufResult.Error!;

        activity?.SetStatus(ActivityStatusCode.Error);
        activity?.SetTag(GatewayActivityTags.ErrorMessage, worst.Message);
        return worst;
    }

    // ── 事件通知（不阻塞主流程）──
    private async Task NotifySinksAsync(Guid deviceId, IReadOnlyList<PointSnapshot> snapshots, CancellationToken ct)
    {
        if (!_sinks.Any()) return;

        var e = new PointStoredEvent { DeviceId = deviceId, Snapshots = snapshots };
        foreach (var sink in _sinks)
        {
            try { await sink.OnStoredAsync(e, ct); }
            catch { /* sink 异常不影响采集 */ }
        }
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
                PointName = "",
                Value = s.Value,
                DataType = DataType.Float,
                Timestamp = s.Timestamp,
                ReceivedAt = now,
                Quality = s.Quality
            }).ToList()
        };
    }
}
