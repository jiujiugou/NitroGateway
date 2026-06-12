namespace NitroGateway.Domain.Protocols;

/// <summary>协议驱动连接状态</summary>
public enum DriverState
{
    /// <summary>未连接，初始状态</summary>
    Disconnected,

    /// <summary>正在建立连接</summary>
    Connecting,

    /// <summary>已连接，可正常读写</summary>
    Connected,

    /// <summary>发生故障（连接中断或协议错误），需要重连</summary>
    Faulted
}
