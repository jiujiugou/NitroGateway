using NitroGateway.Domain.Devices;
using System;
using System.Collections.Generic;
using System.Text;

namespace NitroGateway.Collection
{
    /// <summary>
    /// 设备采集器。对单台设备执行"读→转换→分发→健康上报"流水线，或对全部非维护设备执行一轮并发采集。
    /// 由 <see cref="CollectionEngine"/> 每轮经 DI scope 解析，Scoped 生命周期。
    /// </summary>
    public interface IDeviceCollector
    {
        /// <summary>
        /// 对单台设备执行一轮完整采集（含熔断检查），不抛异常（内部处理并记录）。
        /// </summary>
        /// <param name="device">目标设备</param>
        /// <param name="ct">取消令牌</param>
        public Task CollectDeviceAsync(Device device, CancellationToken ct);

        /// <summary>
        /// 执行一轮全量采集：获取所有设备（含 Offline），过滤维护模式，按并发上限并行采集。
        /// </summary>
        /// <param name="ct">取消令牌；取消时未启动的设备不再启动，已启动的随令牌取消</param>
        public Task CollectOnceAsync(CancellationToken ct);
    }
}
