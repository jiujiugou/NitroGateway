using NitroGateway.Domain.Devices;

namespace NitroGateway.Desktop.Services;

/// <summary>连接测试结果（对齐 Web ADR-023：Connect + Ping 双验）。</summary>
public sealed record ConnectionTestResult(bool Success, long LatencyMs, string? Error, string? Ping = null);

/// <summary>
/// 设备连接测试抽象（ADR-044）。
/// 连接测试是边缘物理操作，Web 中心形态已显式拒绝（400，见 DevicesController.TestConnection）；
/// 桌面端复用协议驱动在本机做 Connect+Ping，供设备编辑窗口「测试连接」按钮调用。
/// </summary>
public interface IDeviceConnectionTester
{
    /// <summary>
    /// 测试设备连接。语义与 Web DevicesController.TestConnection 完全一致：
    /// 先 ConnectAsync 打通链路/串口，再 PingAsync 发最小读请求确认从站存在，
    /// 避免「链路通但从站不存在」的假阳性（ADR-023）。
    /// </summary>
    /// <param name="device">待测试设备（协议 + 连接参数）</param>
    /// <param name="ct">取消令牌</param>
    Task<ConnectionTestResult> TestAsync(Device device, CancellationToken ct = default);
}
