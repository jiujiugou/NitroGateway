using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NitroGateway.Domain.Devices;
using NitroGateway.Shared;
using NitroGateway.Storage.TimeSeries;
using System.Threading.Channels;

namespace NitroGateway.Collection
{
    public sealed class MeasurementWriteHost : BackgroundService
    {
        private readonly Channel<IReadOnlyList<PointSnapshot>> _channel;
        private readonly IMeasurementStore _store;
        private readonly ILogger<MeasurementWriteHost> _logger;
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

        public bool Post(IReadOnlyList<PointSnapshot> snapshots)
        {
             return _channel.Writer.TryWrite(snapshots);   
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (await _channel.Reader.WaitToReadAsync(stoppingToken))
            {
                while (_channel.Reader.TryRead(out var snapshots))
                {
                    
                    try
                    {
                        await _store.WriteAsync(snapshots, stoppingToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogError(ex, "时序库写入失败，跳过本批，继续消费。");
                    }
                }
            }
        }
    }
}