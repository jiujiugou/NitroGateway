namespace NitroGateway.Domain.Measurements;

/// <summary>
/// 批量测点记录，代表一轮采集扫描产生的全部点位数据。
/// 用于将一次扫描周期的结果作为一个整体进行传输和存储。
/// </summary>
public sealed record BatchMeasurements
{
    /// <summary>批次唯一标识</summary>
    public Guid Id { get; init; }

    /// <summary>所属设备 ID</summary>
    public Guid DeviceId { get; init; }

    /// <summary>本次扫描开始时间</summary>
    public DateTime ScanStartedAt { get; init; }

    /// <summary>本次扫描结束时间</summary>
    public DateTime ScanCompletedAt { get; init; }

    /// <summary>本轮采集产生的全部测点记录</summary>
    public IReadOnlyList<MeasurementRecord> Records { get; init; } = Array.Empty<MeasurementRecord>();

    /// <summary>成功采集的点位数</summary>
    public int SuccessCount => Records.Count(r => r.Quality == Devices.QualityCode.Good);

    /// <summary>采集失败的点位数</summary>
    public int FailCount => Records.Count - SuccessCount;
}
