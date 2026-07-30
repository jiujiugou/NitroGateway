using System.Diagnostics;
using NitroGateway.Domain.Protocols;
using NitroGateway.Shared;
using DomainDevice = NitroGateway.Domain.Devices.Device;
using NitroGateway.Protocols;
using NitroGateway.Telemetry.Tracing;
using Microsoft.Extensions.Logging;

namespace NitroGateway.Collection;

/// <summary>
/// 设备数据读取器。
/// 每轮采集通过 <see cref="IProtocolDriverFactory"/> 创建协议驱动实例，
/// 由 <see cref="ReliableProtocolDriver"/> 内部处理建连/重试/断连，读完即释放。
/// </summary>
/// <remarks>
/// <para><b>设计决策：短连接模式，不缓存驱动。</b></para>
/// <para>
/// 每轮采集新建一个驱动实例，读完后通过 <c>using</c> 立即释放。
/// <see cref="ReliableProtocolDriver"/> 的 Polly 管线（3次重试 + 指数退避 + 5s超时）
/// 已经覆盖了连接管理、失败重试和超时保护，不需要额外的全局连接池。
/// </para>
/// <para><b>为什么不用长连接？</b></para>
/// <list type="bullet">
/// <item>Modbus TCP / S7 握手开销 &lt;5ms，1000ms 采集间隔下可忽略。</item>
/// <item>无状态：不需要处理连接缓存同步、设备 CRUD 变更、TCP 半开检测。</item>
/// <item>工厂 + using 可确保每次读写后 TCP socket 立即回收，不堆积。</item>
/// </list>
/// </remarks>
public sealed class DeviceReader : IDeviceReader
{
    private readonly IProtocolDriverFactory _driverFactory;
    private readonly ILogger<DeviceReader> _logger;

    /// <summary>创建数据读取器</summary>
    /// <param name="driverFactory">协议驱动工厂，按设备协议和连接参数创建对应驱动</param>
    /// <param name="logger">日志记录器</param>
    public DeviceReader(IProtocolDriverFactory driverFactory, ILogger<DeviceReader> logger)
    {
        _driverFactory = driverFactory;
        _logger = logger;
    }

    /// <summary>
    /// 对单台设备执行一轮采集。
    /// 流程：取启用点位 → 工厂创建驱动 → 批量读取 → using 释放驱动。
    /// </summary>
    /// <param name="device">目标设备（含协议、连接参数、点位列表）</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>原始点位值列表；设备无启用点位时返回空列表</returns>
    public async Task<OperationResult<IReadOnlyList<RawPointValue>>> ReadDeviceAsync(
    DomainDevice device,
    CancellationToken ct)
    {
        using var activity = GatewayActivitySource.Source.StartActivity(GatewayActivities.ReadDevice);
        activity?.SetTag(GatewayActivityTags.DeviceId, device.Id.ToString());
        activity?.SetTag(GatewayActivityTags.DeviceProtocol, device.Protocol.Name);

        _logger.LogInformation("开始读取设备：{Device}", device.Name);

        var points = device.Points.Where(p => p.Enabled).ToList();

        if (points.Count == 0)
            return Array.Empty<RawPointValue>();

        try
        {
            // 每轮新建驱动 → ReliableProtocolDriver 自动建连/重试/断连
            using var driver = _driverFactory.Create(device.Protocol, device.Connection);

            var result = await driver.ReadBatchAsync(points, ct);

            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "设备 {Device} 读取异常",
                device.Name);

            return OperationalError.Protocol(
                $"设备读取异常：{ex.Message}");
        }
    }

}
