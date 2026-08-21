using System.Collections.Concurrent;
using System.Diagnostics;
using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Protocols;
using NitroGateway.Shared;
using DomainDevice = NitroGateway.Domain.Devices.Device;
using NitroGateway.Protocols;
using NitroGateway.Telemetry.Tracing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NitroGateway.Collection;

/// <summary>
/// 设备数据读取器。
/// 通过 <see cref="IProtocolDriverPool"/> 按设备复用长连接驱动，
/// 连接参数不变时保持 socket/串口打开，避免每轮采集反复握手与关闭；
/// 通信失败由 <see cref="ReliableProtocolDriver"/> 自动建连/重试恢复。
/// <para><b>边界：</b>只取 <c>Enabled = true</c> 的点位；无启用点位直接返回空列表，
/// 不视为错误。协议层异常被转换为 <see cref="OperationResult"/> 失败而非抛出。</para>
/// <para><b>点位级采样调度（ADR-062）：</b>按 <c>DevicePoint.ScanIntervalMs</c> 筛选到期点位，
/// 每轮只把到期点子集传给驱动批量读取。上次采集时间缓存在本 Singleton 实例的
/// <see cref="ConcurrentDictionary{TKey,TValue}"/> 中（DeviceCollector 是 Scoped 每轮新建，
/// 放它身上每轮重置会永不生效）；进程重启自然清空 → 首次/新点位立即读，与 ChangeDetector 一致。</para>
/// </summary>
public sealed class DeviceReader : IDeviceReader
{
    private readonly IProtocolDriverPool _driverPool;
    private readonly ILogger<DeviceReader> _logger;
    /// <summary>全局默认采集间隔（<c>Collection:IntervalMs</c>，默认 1000ms）；点位 <c>ScanIntervalMs=0</c> 时继承。</summary>
    private readonly TimeSpan _defaultInterval;
    /// <summary>时钟注入点，测试可替换为可控时钟；默认 <see cref="DateTime.UtcNow"/>。</summary>
    private readonly Func<DateTime> _utcNow;
    /// <summary>点位 ID → 上次采集时间（UTC）。Singleton 常驻；仅追加、随进程重启清空。</summary>
    private readonly ConcurrentDictionary<Guid, DateTime> _lastScannedAt = new();

    /// <summary>创建数据读取器</summary>
    /// <param name="driverPool">协议驱动连接池，按设备缓存/复用驱动</param>
    /// <param name="options">采集配置；<c>ScanIntervalMs=0</c> 的点位继承 <c>IntervalMs</c></param>
    /// <param name="logger">日志记录器</param>
    /// <param name="utcNow">时钟注入（测试用）；缺省 <see cref="DateTime.UtcNow"/></param>
    public DeviceReader(
        IProtocolDriverPool driverPool,
        IOptions<CollectionOption> options,
        ILogger<DeviceReader> logger,
        Func<DateTime>? utcNow = null)
    {
        _driverPool = driverPool;
        _defaultInterval = TimeSpan.FromMilliseconds(options.Value.IntervalMs);
        _logger = logger;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <inheritdoc />
    /// <remarks>
    /// 纯查询：不更新 <see cref="_lastScannedAt"/>（更新在 <see cref="ReadDeviceAsync"/> 实际读取时）。
    /// 负数 <c>ScanIntervalMs</c> 已在配置/UI 层拦截（PointEditor/PointManager），此处按
    /// <c>&lt;= 0 → 继承全局间隔</c> 兜底，保证永不出现非法负间隔。
    /// </remarks>
    public IReadOnlyList<DevicePoint>? GetDuePoints(DomainDevice device)
    {
        var enabled = device.Points.Where(p => p.Enabled).ToList();
        if (enabled.Count == 0)
            return null; // 无 enabled 点位 → 仍需真实探活（ADR-031），由 ReadDeviceAsync 走空列表探测

        var now = _utcNow();
        var due = new List<DevicePoint>(enabled.Count);
        foreach (var p in enabled)
        {
            var interval = p.ScanIntervalMs > 0
                ? TimeSpan.FromMilliseconds(p.ScanIntervalMs)
                : _defaultInterval;
            // 首次启动/新点位（无历史）→ 立即读；否则按「上次采集 + 间隔」判定是否到期
            if (!_lastScannedAt.TryGetValue(p.Id, out var last) || now - last >= interval)
                due.Add(p);
        }
        return due;
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

        // ADR-062：按点位级 ScanIntervalMs 筛选到期点位。
        // 全部未到期 → 空成功（正常流程由 DeviceCollector 提前拦截跳过，此处兜底）；
        // null（无 enabled 点）→ 空列表探活（ADR-031，现状不变）。
        var due = GetDuePoints(device);
        if (due is { Count: 0 })
        {
            _logger.LogDebug("设备 {Device} 全部点位未到采样间隔，跳过读取", device.Name);
            return OperationResult<IReadOnlyList<RawPointValue>>.Success([]);
        }
        var points = due ?? device.Points.Where(p => p.Enabled).ToList();

        // 标记本轮已扫描：无论读取成败，间隔内不再触发——降频点位失败时不会退化为每秒重试
        var scannedAt = _utcNow();
        foreach (var p in points)
            _lastScannedAt[p.Id] = scannedAt;

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
