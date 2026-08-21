using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Protocols;
using NitroGateway.Protocols;
using NitroGateway.Protocols.OpcUa;
using NitroGateway.Shared;
using Xunit;

namespace NitroGateway.UnitTests;

/// <summary>
/// ADR-030 P1：ProtocolDriverFactory 将 DeviceConnection.RetryCount/RetryIntervalMs
/// 注入 ReliableProtocolDriver（此前硬编码 3 次/500ms，连接参数配置不生效）。
/// </summary>
public class ProtocolDriverFactoryTests
{
    [Fact]
    public void Create_UsesConnectionRetryCount_RetriesConfiguredTimes()
    {
        var inner = new FailingInner { FailuresRemaining = 2 };
        var factory = BuildFactory(inner);

        var driver = factory.Create(
            new ProtocolIdentifier { Name = "Fake", Dialect = "TCP" },
            new DeviceConnection { Endpoint = "127.0.0.1:502", RequestTimeoutMs = 5000, RetryCount = 2, RetryIntervalMs = 1 });

        var result = driver.ReadBatchAsync([]).GetAwaiter().GetResult();

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(3, inner.ReadCalls); // 初始 1 + 配置重试 2
    }

    [Fact]
    public void Create_RetryCountZero_DoesNotRetry()
    {
        var inner = new FailingInner { FailuresRemaining = 1 };
        var factory = BuildFactory(inner);

        var driver = factory.Create(
            new ProtocolIdentifier { Name = "Fake", Dialect = "TCP" },
            new DeviceConnection { Endpoint = "127.0.0.1:502", RequestTimeoutMs = 5000, RetryCount = 0, RetryIntervalMs = 1 });

        var result = driver.ReadBatchAsync([]).GetAwaiter().GetResult();

        Assert.True(result.IsFailure);
        Assert.Equal(1, inner.ReadCalls);
    }

    /// <summary>
    /// OPC UA 接入冒烟（12-OPC-UA接入设计.md S5）：OpcUaRegistration 注册的驱动可被复合工厂创建，
    /// 返回 ReliableProtocolDriver 装饰器且 Capability 透传内层（批量读/订阅能力可用）。
    /// </summary>
    [Fact]
    public void Create_OpcUa_Registered_ReturnsDecoratorWithCapabilities()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var provider = services.BuildServiceProvider();
        var factory = new ProtocolDriverFactory(provider);
        OpcUaRegistration.Register(factory);

        var driver = factory.Create(
            ProtocolIdentifier.OpcUa,
            new DeviceConnection { Endpoint = "opc.tcp://127.0.0.1:4840", RequestTimeoutMs = 5000 });

        Assert.True(driver.Capability.SupportsBatchRead);
        Assert.True(driver.Capability.SupportsBatchWrite);
        Assert.True(driver.Capability.SupportsSubscription);
        Assert.Equal(DriverState.Disconnected, driver.State);
    }

    private static IProtocolDriverFactory BuildFactory(FailingInner inner)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var provider = services.BuildServiceProvider();
        var factory = new ProtocolDriverFactory(provider);
        factory.Register("Fake", (_, _, _) => inner);
        return factory;
    }

    /// <summary>可编程内层驱动：前 FailuresRemaining 次失败，之后成功；计数 ReadCalls。</summary>
    private sealed class FailingInner : IProtocolDriver
    {
        public int ReadCalls { get; private set; }
        public int FailuresRemaining { get; set; }
        public DriverState State => DriverState.Connected;
        public DriverCapability Capability => new();

        public Task<OperationResult> ConnectAsync(CancellationToken ct = default) => Task.FromResult(OperationResult.Success());
        public Task<OperationResult> DisconnectAsync(CancellationToken ct = default) => Task.FromResult(OperationResult.Success());
        public Task<OperationResult> PingAsync(CancellationToken ct = default) => Task.FromResult(OperationResult.Success());
        public Task<OperationResult<RawPointValue>> ReadAsync(DevicePoint point, CancellationToken ct = default)
            => Task.FromResult(OperationResult<RawPointValue>.Success(new RawPointValue { Point = point, Value = 1f, Timestamp = DateTime.UtcNow }));

        public Task<OperationResult<IReadOnlyList<RawPointValue>>> ReadBatchAsync(
            IEnumerable<DevicePoint> points, CancellationToken ct = default)
        {
            ReadCalls++;
            if (FailuresRemaining > 0)
            {
                FailuresRemaining--;
                return Task.FromResult(OperationResult<IReadOnlyList<RawPointValue>>.Failure(OperationalError.Protocol("设备无响应")));
            }
            return Task.FromResult(OperationResult<IReadOnlyList<RawPointValue>>.Success(new List<RawPointValue>()));
        }

        public Task<OperationResult> WriteAsync(DevicePoint point, object value, CancellationToken ct = default)
            => Task.FromResult(OperationResult.Success());
        public Task<OperationResult> WriteBatchAsync(IEnumerable<KeyValuePair<DevicePoint, object>> entries, CancellationToken ct = default)
            => Task.FromResult(OperationResult.Success());
        public void Dispose() { }
    }
}
