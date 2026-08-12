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
/// 通过 <see cref="IProtocolDriverPool"/> 按设备复用长连接驱动，
/// 连接参数不变时保持 socket/串口打开，避免每轮采集反复握手与关闭；
/// 通信失败由 <see cref="ReliableProtocolDriver"/> 自动建连/重试恢复。
/// <para><b>边界：</b>只取 <c>Enabled = true</c> 的点位；无启用点位直接返回空列表，
/// 不视为错误。协议层异常被转换为 <see cref="OperationResult"/> 失败而非抛出。</para>
/// </summary>
public sealed class DeviceReader : IDeviceReader
{
    private readonly IProtocolDriverPool _driverPool;
    private readonly ILogger<DeviceReader> _logger;

    /// <summary>创建数据读取器</summary>
    /// <param name="driverPool">协议驱动连接池，按设备缓存/复用驱动</param>
    /// <param name="logger">日志记录器</param>
    public DeviceReader(IProtocolDriverPool driverPool, ILogger<DeviceReader> logger)
    {
        _driverPool = driverPool;
        _logger = logger;
    }

    /// <summary>
    /// 对单台设备执行一轮采集。
    /// 流程：取启用点位 → 从池中获取驱动 → 批量读取。
    /// </summary>
    /// <param name="device">目标设备（含协议、连接参数、点位列表）</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>原始点位值列表；设备无启用点位时仍会尝试连接，连接成功返回空列表，连接失败返回错误</returns>
    public async Task<OperationResult<IReadOnlyList<RawPointValue>>> ReadDeviceAsync(
    DomainDevice device,
    CancellationToken ct)
    {
        using var activity = GatewayActivitySource.Source.StartActivity(GatewayActivities.ReadDevice);
        activity?.SetTag(GatewayActivityTags.DeviceId, device.Id.ToString());
        activity?.SetTag(GatewayActivityTags.DeviceProtocol, device.Protocol.Name);

        // ADR-019 P2-5：每轮采集日志降 Debug（离线设备 N 台 × 每秒一行 Info 刷屏）
        _logger.LogDebug("开始读取设备：{Device}", device.Name);

        var points = device.Points.Where(p => p.Enabled).ToList();

        // ADR-030 L2（用户决策）+ ADR-031：空点位设备不跳过——仍从连接池取驱动并尝试连接/复用长连接；
        // 驱动层对空点位列表先发真实探测读（Modbus 寄存器 0 / S7 PingAddress）验证链路，成功才返回空列表，失败上报判离线

        try
        {
            // 复用池中的长连接驱动；断线恢复由 ReliableProtocolDriver 的建连/重试管线负责
            var driver = _driverPool.GetOrCreate(device);
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
