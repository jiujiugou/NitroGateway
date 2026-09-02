using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Protocols;
using NitroGateway.Protocols;

namespace NitroGateway.Collection;

/// <summary>协调支持订阅的协议驱动与既有采集数据管道。</summary>
public interface ISubscriptionCoordinator
{
    /// <summary>订阅已生效时返回 true；false 表示调用方应继续轮询。</summary>
    Task<bool> TryActivateAsync(Device device, CancellationToken ct);
}

/// <summary>
/// 每台支持 Subscription 的设备维持一个订阅，并将通知重新接入既有 Pipeline/Dispatcher。
/// 订阅启动失败是可恢复的能力降级，不阻断本轮轮询。
/// <paramref name="driverPool"/> 为 null（未注册协议层，如部分测试宿主）时视为不支持订阅，
/// <see cref="TryActivateAsync"/> 恒返回 false，采集保持轮询兜底。
/// </summary>
public sealed class SubscriptionCoordinator : ISubscriptionCoordinator
{
    private readonly IProtocolDriverPool? _driverPool;
    private readonly IPointValuePipeline _pipeline;
    private readonly IDataDispatcher _dispatcher;
    private readonly IHealthReporter _reporter;
    private readonly int _publishingIntervalMs;
    private readonly ILogger<SubscriptionCoordinator> _logger;
    private readonly ConcurrentDictionary<Guid, Binding> _bindings = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _deviceGates = new();

    public SubscriptionCoordinator(
        IProtocolDriverPool? driverPool,
        IPointValuePipeline pipeline,
        IDataDispatcher dispatcher,
        IHealthReporter reporter,
        IOptions<CollectionOption> options,
        ILogger<SubscriptionCoordinator> logger)
    {
        _driverPool = driverPool;
        _pipeline = pipeline;
        _dispatcher = dispatcher;
        _reporter = reporter;
        _publishingIntervalMs = options.Value.IntervalMs;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<bool> TryActivateAsync(Device device, CancellationToken ct)
    {
        if (_driverPool is null)
            return false;

        var points = device.Points.Where(point => point.Enabled).ToArray();
        if (points.Length == 0)
            return false;

        var driver = _driverPool.GetOrCreate(device);
        if (!driver.Capability.SupportsSubscription || driver is not ISubscriptionSource source)
            return false;

        var gate = _deviceGates.GetOrAdd(device.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (_bindings.TryGetValue(device.Id, out var existing) && !ReferenceEquals(existing.Source, source))
            {
                existing.Source.ValuesReceived -= existing.Handler;
                _bindings.TryRemove(device.Id, out _);
            }

            var binding = _bindings.GetOrAdd(device.Id, _ =>
            {
                Func<IReadOnlyList<RawPointValue>, Task> handler = values =>
                    DispatchValuesAsync(device.Id, device.Name, values);
                source.ValuesReceived += handler;
                return new Binding(source, handler);
            });

            var result = await source.EnsureSubscriptionAsync(points, _publishingIntervalMs, ct);
            if (result.IsSuccess && source.IsSubscriptionActive)
                return true;

            source.ValuesReceived -= binding.Handler;
            _bindings.TryRemove(device.Id, out _);
            _logger.LogWarning("设备 {Device} OPC UA 订阅未生效，将回退轮询: {Error}",
                device.Name, result.Error?.Message ?? "订阅状态未激活");
            return false;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task DispatchValuesAsync(
        Guid deviceId,
        string deviceName,
        IReadOnlyList<RawPointValue> values)
    {
        var snapshots = _pipeline.Process(deviceId, values);
        if (snapshots.Count > 0)
            await _dispatcher.DispatchAsync(deviceId, snapshots, CancellationToken.None);
        _reporter.Report(deviceId, deviceName, true, null);
    }

    private sealed record Binding(
        ISubscriptionSource Source,
        Func<IReadOnlyList<RawPointValue>, Task> Handler);
}
