namespace NitroGateway.Collection;

/// <summary>
/// 健康上报器。
/// 将单次设备采集结果（成功点数、失败点数、错误信息）汇总后上报给 DeviceHealthMonitor，
/// 由 DeviceHealthMonitor 根据连续失败/成功策略进行设备健康状态判定和状态迁移。
/// </summary>
public interface IHealthReporter
{
    /// <summary>
    /// 上报一次采集结果。
    /// </summary>
    /// <param name="deviceId">设备唯一标识。</param>
    /// <param name="deviceName">设备名称；用于健康变更日志定位设备，可能为空。</param>
    /// <param name="successCount">本次采集中成功处理的数据点数量。</param>
    /// <param name="failCount">本次采集中失败的数据点数量。</param>
    /// <param name="errorMessage">采集失败原因；无错误时为空。</param>
    void Report(
        Guid deviceId,
        string? deviceName,
        int successCount,
        int failCount,
        string? errorMessage);
}
