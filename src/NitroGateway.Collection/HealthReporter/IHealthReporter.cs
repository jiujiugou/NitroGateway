namespace NitroGateway.Collection;

/// <summary>
/// 健康上报器。将单次设备采集结果（链路成功/失败）汇总后上报给 DeviceHealthMonitor，
/// 由 DeviceHealthMonitor 根据连续失败/成功策略进行设备健康状态判定和状态迁移。
/// </summary>
public interface IHealthReporter
{
    /// <summary>
    /// 上报一次采集结果。
    /// </summary>
    /// <param name="deviceId">设备唯一标识。</param>
    /// <param name="deviceName">设备名称；用于健康变更日志定位设备，可能为空。</param>
    /// <param name="succeeded">本轮采集链路是否成功（连接+读取）；点位级质量/转换失败不影响设备健康。</param>
    /// <param name="errorMessage">失败原因；succeeded=false 时透传给健康监控 LastError，为 true 时仅作诊断信息。</param>
    void Report(Guid deviceId, string? deviceName, bool succeeded, string? errorMessage);
}
