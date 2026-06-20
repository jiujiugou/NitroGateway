using Microsoft.Extensions.Logging;
using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Measurements;
using NitroGateway.Shared;
using NitroGateway.Storage.Buffer;
using NitroGateway.Storage.TimeSeries;

namespace NitroGateway.Collection;

/// <summary>数据分发实现。双写时序库 + 转发缓冲，互不阻塞</summary>
public sealed class DataDispatcher : IDataDispatcher
{
    private readonly IMeasurementStore _timeSeries;
    private readonly IForwardBuffer _buffer;
    private readonly ILogger<DataDispatcher> _logger;

    public DataDispatcher(
        IMeasurementStore timeSeries,
        IForwardBuffer buffer,
        ILogger<DataDispatcher> logger)
    {
        _timeSeries = timeSeries;
        _buffer = buffer;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<OperationResult> DispatchAsync(
        Guid deviceId, IReadOnlyList<PointSnapshot> snapshots, CancellationToken ct)
    {
        if (snapshots.Count == 0) return OperationResult.Success();

        var tsOk = false;
        var bufOk = false;

        // 写时序库
        var tsResult = await _timeSeries.WriteAsync(snapshots, ct);
        if (tsResult.IsSuccess) tsOk = true;
        else _logger.LogWarning("时序写入失败: {Error}", tsResult.Error!.Message);

        // 入转发缓冲
        var batch = ToBatchMeasurements(deviceId, snapshots);
        var bufResult = await _buffer.EnqueueAsync(batch, ct);
        if (bufResult.IsSuccess) bufOk = true;
        else _logger.LogWarning("缓冲入队失败: {Error}", bufResult.Error!.Message);

        return tsOk && bufOk
            ? OperationResult.Success()
            : OperationalError.General(
                $"分发部分失败: 时序={(tsOk ? "OK" : "FAIL")} 缓冲={(bufOk ? "OK" : "FAIL")}");
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
