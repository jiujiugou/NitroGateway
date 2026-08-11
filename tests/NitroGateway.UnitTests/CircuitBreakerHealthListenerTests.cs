using NitroGateway.Collection;
using NitroGateway.DeviceManagement.Events;
using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Protocols;
using NitroGateway.Protocols;
using Xunit;

namespace NitroGateway.UnitTests;

/// <summary>
/// ADR-030 P2：设备健康判定 Offline 时，熔断器 Trip 之外还应 Evict 连接池，
/// 释放全失败后滞留的 Faulted 驱动/socket；Online 恢复只 Reset 不 Evict。
/// </summary>
public class CircuitBreakerHealthListenerTests
{
    [Fact]
    public void OnOffline_TripsBreaker_AndEvictsPoolConnection()
    {
        var breakers = new FakeBreakerRegistry();
        var pool = new FakeDriverPool();
        var listener = new CircuitBreakerHealthListener(breakers, pool);
        var deviceId = Guid.NewGuid();

        listener.OnHealthChangedAsync(new DeviceHealthChanged
        {
            DeviceId = deviceId,
            OldStatus = DeviceStatus.Unknown,
            NewStatus = DeviceStatus.Offline
        });

        Assert.True(breakers.Tripped);
        Assert.Equal(deviceId, pool.Evicted);
    }

    [Fact]
    public void OnOnline_ResetsBreaker_DoesNotEvict()
    {
        var breakers = new FakeBreakerRegistry();
        var pool = new FakeDriverPool();
        var listener = new CircuitBreakerHealthListener(breakers, pool);
        var deviceId = Guid.NewGuid();

        listener.OnHealthChangedAsync(new DeviceHealthChanged
        {
            DeviceId = deviceId,
            OldStatus = DeviceStatus.Unknown,
            NewStatus = DeviceStatus.Online
        });

        Assert.True(breakers.ResetCalled);
        Assert.Null(pool.Evicted);
    }

    private sealed class FakeBreakerRegistry : ICircuitBreakerRegistry
    {
        public bool Tripped { get; private set; }
        public bool ResetCalled { get; private set; }

        public ICircuitBreaker Get(Guid deviceId)
            => new FakeBreaker(() => Tripped = true, () => ResetCalled = true);

        public void Reset(Guid deviceId) => ResetCalled = true;
        public IReadOnlyDictionary<Guid, ICircuitBreaker> GetAll() => new Dictionary<Guid, ICircuitBreaker>();
    }

    private sealed class FakeBreaker : ICircuitBreaker
    {
        private readonly Action _onTrip;
        private readonly Action _onReset;
        public FakeBreaker(Action onTrip, Action onReset) { _onTrip = onTrip; _onReset = onReset; }
        public CircuitState State => CircuitState.Closed;
        public bool TryEnterProbe() => true;
        public void RecordSuccess() { }
        public void RecordFailure() { }
        public void Trip() => _onTrip();
        public void Reset() => _onReset();
    }

    private sealed class FakeDriverPool : IProtocolDriverPool
    {
        public Guid? Evicted { get; private set; }
        public IProtocolDriver GetOrCreate(Device device) => throw new NotSupportedException();
        public void Evict(Guid deviceId) => Evicted = deviceId;
        public void Dispose() { }
    }
}
