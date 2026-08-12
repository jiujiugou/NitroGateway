namespace NitroGateway.Domain.Measurements;

/// <summary>
/// 批量测点记录，代表一轮采集扫描产生的全部点位数据。
/// 用于将一次扫描周期的结果作为一个整体进行传输和存储。
/// </summary>
public sealed record BatchMeasurements
{
    /// <summary>
    /// 站点标识（ADR-035 第 1 步）：随负载上行，HTTP 通道等无 topic 场景也能区分站点。
    /// MQTT 通道同时以 topic 第三层承载（<c>nitrogateway/{siteId}/{deviceId}/measurements</c>），
    /// 中心 Ingest 以 topic 为准、负载字段作为冗余校验。
    /// </summary>
    public string SiteId { get; init; } = "";

    /// <summary>
    /// 载荷版本（ADR-025 P1）。当前版本为 1。
    /// 序列化输出顶层字段 <c>v</c>；旧版载荷无此字段，反序列化得 0，按 v1 兼容读取。
    /// </summary>
    public int V { get; init; } = 1;

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
