namespace NitroGateway.Domain.Devices;

/// <summary>设备通信状态</summary>
public enum DeviceStatus
{
    /// <summary>尚未进行首次通信探测，状态未知</summary>
    Unknown,

    /// <summary>通信正常</summary>
    Online,

    /// <summary>通信中断（网络不可达或设备无响应）</summary>
    Offline,

    /// <summary>通信异常（超时、协议错误、校验失败等）</summary>
    Error,

    /// <summary>维护中，暂停采集与告警</summary>
    Maintenance
}
