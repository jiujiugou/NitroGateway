using NitroGateway.Desktop.Services.Connectivity;
using NitroGateway.Domain.Devices;
using NitroGateway.Shared;
using Xunit;

namespace NitroGateway.UnitTests;

public sealed class DeviceConnectionTesterTests
{
    private static Device ModbusDevice() => new()
    {
        Id = Guid.NewGuid(),
        Name = "PLC-1",
        Protocol = new ProtocolIdentifier { Name = "Modbus", Dialect = "TCP" },
        Connection = new DeviceConnection
        {
            Endpoint = "127.0.0.1:502",
            RetryCount = 3,
            RetryIntervalMs = 1000
        }
    };

    [Fact]
    public async Task TestAsync_connect_and_ping_ok_returns_success()
    {
        var tester = new DeviceConnectionTester(new FakeDriverFactory(
            new FakeProtocolDriver(OperationResult.Success(), OperationResult.Success())));

        var result = await tester.TestAsync(ModbusDevice());

        Assert.True(result.Success);
        Assert.Equal("ok", result.Ping);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task TestAsync_connect_ok_ping_fail_returns_failure()
    {
        var tester = new DeviceConnectionTester(new FakeDriverFactory(
            new FakeProtocolDriver(OperationResult.Success(), OperationalError.Timeout("从站无响应"))));

        var result = await tester.TestAsync(ModbusDevice());

        Assert.False(result.Success);
        Assert.Contains("从站无响应", result.Error);
    }

    [Fact]
    public async Task TestAsync_connect_fail_returns_failure()
    {
        var tester = new DeviceConnectionTester(new FakeDriverFactory(
            new FakeProtocolDriver(OperationalError.Communication("拒绝连接"), OperationResult.Success())));

        var result = await tester.TestAsync(ModbusDevice());

        Assert.False(result.Success);
        Assert.Contains("拒绝连接", result.Error);
    }

    [Fact]
    public async Task TestAsync_unsupported_protocol_returns_failure_not_throw()
    {
        // 工厂 Create 抛异常（未注入 driver）→ 测试器捕获并转失败结果，不向上抛
        var tester = new DeviceConnectionTester(new FakeDriverFactory());
        var device = ModbusDevice();
        device.Protocol = new ProtocolIdentifier { Name = "Bogus" };

        var result = await tester.TestAsync(device);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }
}
