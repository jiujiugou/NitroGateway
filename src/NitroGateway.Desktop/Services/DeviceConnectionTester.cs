using System.Diagnostics;
using NitroGateway.Domain.Devices;
using NitroGateway.Protocols;

namespace NitroGateway.Desktop.Services;

/// <summary>
/// 桌面端连接测试实现（ADR-044/ADR-023）。
/// 构造注入 <see cref="IProtocolDriverFactory"/>（桌面 GatewayHost 已注册 AddNitroProtocol），
/// 与采集引擎共用同一驱动实现，保证「测试结果 = 实际采集同一条链路」。
/// </summary>
public sealed class DeviceConnectionTester : IDeviceConnectionTester
{
    private readonly IProtocolDriverFactory _driverFactory;

    public DeviceConnectionTester(IProtocolDriverFactory driverFactory)
    {
        _driverFactory = driverFactory;
    }

    /// <inheritdoc />
    public async Task<ConnectionTestResult> TestAsync(Device device, CancellationToken ct = default)
    {
        // 连接测试不重试：RetryCount/RetryIntervalMs 置 0，避免失败重试拖长等待（对齐 Web 语义）。
        var connection = device.Connection with { RetryCount = 0, RetryIntervalMs = 0 };

        try
        {
            using var driver = _driverFactory.Create(device.Protocol, connection);
            var sw = Stopwatch.StartNew();
            var connectResult = await driver.ConnectAsync(ct);

            if (!connectResult.IsSuccess)
            {
                sw.Stop();
                return new ConnectionTestResult(false, sw.ElapsedMilliseconds, connectResult.Error?.Message ?? "连接失败");
            }

            // ADR-023：连接成功只代表链路/串口已通，不代表目标从站存在；
            // 必须 Ping（最小读请求）确认从站响应，否则对 UnitId 校验型从站是假阳性。
            var pingResult = await driver.PingAsync(ct);
            sw.Stop();
            return pingResult.IsSuccess
                ? new ConnectionTestResult(true, sw.ElapsedMilliseconds, null, "ok")
                : new ConnectionTestResult(false, sw.ElapsedMilliseconds, pingResult.Error?.Message ?? "从站无响应");
        }
        catch (Exception ex)
        {
            return new ConnectionTestResult(false, 0, ex.Message);
        }
    }
}
