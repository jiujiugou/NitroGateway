using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NitroGateway.Domain.Devices;
using NitroGateway.Shared;
using NitroGateway.Storage.TimeSeries;
using NitroGateway.Telemetry;
using System.Threading.Channels;

namespace NitroGateway.Collection
{
    /// <summary>
    /// 时序库写入宿主（BackgroundService）。消费有界 Channel，把采集热路径的落库请求异步批量写入
    /// <see cref="IMeasurementStore"/>。
    /// <para><b>边界：</b>Channel 容量 1000 批，满时 <see cref="BoundedChannelFullMode.DropOldest"/>
    /// 丢弃最旧批次（数据按时间戳旧→新有序丢弃，丢最旧优先级最低）；单批写入异常仅记录日志，
    /// 跳过该批继续消费，避免落库故障阻塞采集；停止时先排空队列剩余批次（限时 5s，ADR-016 P2-3）。</para>
    /// </summary>
    public sealed class MeasurementWriteHost : BackgroundService
    {
        /// <summary>停机排空时间上限：防止存储故障把停机拖死。</summary>
        private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(5);

        /// <summary>有界 Channel：容量 1000 批，满时丢弃最旧。</summary>
        private readonly Channel<IReadOnlyList<PointSnapshot>> _channel;
        private readonly IMeasurementStore _store;
        private readonly ILogger<MeasurementWriteHost> _logger;

        /// <summary>创建写入宿主。</summary>
        /// <param name="store">时序库存储实现</param>
        /// <param name="logger">日志记录器</param>
        public MeasurementWriteHost(IMeasurementStore store, ILogger<MeasurementWriteHost> logger)
        {
            _store = store;
            _logger = logger;
            _channel = Channel.CreateBounded<IReadOnlyList<PointSnapshot>>(
                new BoundedChannelOptions(1000)
                {
                    FullMode = BoundedChannelFullMode.DropOldest
                });
        }

        /// <summary>
        /// 非阻塞入队一批快照供后台写入。
        /// </summary>
        /// <param name="snapshots">点位快照列表</param>
        /// <returns>入队成功返回 true；Channel 已满返回 false（由调用方决定告警/丢弃）</returns>
        public bool Post(IReadOnlyList<PointSnapshot> snapshots)
        {
             return _channel.Writer.TryWrite(snapshots);   
        }

        /// <summary>
        /// 后台消费循环：持续从 Channel 读取并写入时序库；停止时排空剩余批次。
        /// </summary>
        /// <param name="stoppingToken">宿主停止令牌；取消时优雅退出循环</param>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                while (await _channel.Reader.WaitToReadAsync(stoppingToken))
                {
                    while (_channel.Reader.TryRead(out var snapshots))
                    {
                        try
                        {
                            var write = await _store.WriteAsync(snapshots, stoppingToken);
                            // ADR-018 P2-1：WriteAsync 失败（如数据库锁定/磁盘满）不再静默丢弃——
                            // 修复前直接忽略 OperationResult，清理窗口内落库批次丢数无任何告警；
                            // 现在记 Error 并上报指标，调用方可见并处置。
                            if (write.IsFailure)
                            {
                                NitroMetrics.StoreWriteFailures.Inc();
                                _logger.LogError(
                                    "时序库写入失败 [{Code}] {Message}，跳过本批，继续消费。",
                                    write.Error!.Code, write.Error.Message);
                            }
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            _logger.LogError(ex, "时序库写入失败，跳过本批，继续消费。");
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // 正常关闭：进入停机排空
            }

            // ADR-016 P2-3：停机排空——队列剩余批次写尽（限时），避免"取消即丢"与注释不符
            var drainDeadline = DateTime.UtcNow + DrainTimeout;
            while (_channel.Reader.TryRead(out var snapshots))
            {
                if (DateTime.UtcNow > drainDeadline)
                {
                    _logger.LogWarning("停机排空超时，剩余批次丢弃");
                    break;
                }
                try
                {
                    await _store.WriteAsync(snapshots, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "停机排空写入失败，跳过本批");
                }
            }
        }
    }
}
