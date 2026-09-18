using NitroGateway.Domain.Devices;

namespace NitroGateway.Desktop.Services.Connectivity;

public sealed record ConnectionTestResult(bool Success, long LatencyMs, string? Error, string? Ping = null);

/// <summary>
/// 设备连接测试抽象（ADR-044）。
/// 连接测试是边缘物理操作，Web 中心形态已显式拒绝（400，见 DevicesController.TestConnection）；
/// 桌面端复用协议驱动在本机做 Connect+Ping，供设备编辑窗口「测试连接」按钮调用。
/// </summary>
public interface IDeviceConnectionTester
{
    Task<ConnectionTestResult> TestAsync(Device device, CancellationToken ct = default);
}
