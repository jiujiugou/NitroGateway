using Microsoft.Extensions.Logging.Abstractions;
using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Protocols;
using NitroGateway.Protocols.OpcUa;
using Xunit;

namespace NitroGateway.UnitTests;

/// <summary>
/// OPC UA 驱动测试（12-OPC-UA接入设计.md S5，无需真实服务器，覆盖未连接失败路径）：
/// 初始状态/能力声明、未连接时读写/Ping 返回 Unavailable（不抛异常、不产伪值）、
/// 空端点 ConnectAsync 返回 Validation 错误。
/// </summary>
public class OpcUaDriverTests
{
    private static OpcUaDriver CreateDriver(DeviceConnection? connection = null)
    {
        var conn = connection ?? new DeviceConnection
        {
            Endpoint = "opc.tcp://127.0.0.1:4840",
            RequestTimeoutMs = 5000
        };
        return new OpcUaDriver(conn, NullLogger.Instance);
    }

    private static DevicePoint Point(string name, string address, DataType type) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Address = address,
        DataType = type
    };

    [Fact]
    public void Ctor_InitialState_Disconnected()
    {
        var driver = CreateDriver();
        Assert.Equal(DriverState.Disconnected, driver.State);
    }

    [Fact]
    public void Capability_SupportsBatchAndSubscription()
    {
        var driver = CreateDriver();
        Assert.True(driver.Capability.SupportsBatchRead);
        Assert.True(driver.Capability.SupportsBatchWrite);
        Assert.True(driver.Capability.SupportsSubscription);
        Assert.True(driver.Capability.SupportsBrowse);   // ADR-070 层次1：OPC UA 支持节点浏览
        Assert.Equal(0, driver.Capability.MaxBatchSize); // 0 = 无限制
    }

    /// <summary>ADR-070 层次1：未连接时浏览返回 Unavailable（与读写一致，不抛异常）</summary>
    [Fact]
    public async Task BrowseAsync_NotConnected_ReturnsUnavailable()
    {
        var driver = CreateDriver();
        var r = await driver.BrowseAsync("ns=2;i=5001");
        Assert.True(r.IsFailure);
        Assert.Equal("ResourceUnavailable", r.Error!.Code);
    }

    /// <summary>ADR-019 P1-1：未连接读单点返回 Unavailable，绝不产出 0.0 伪值</summary>
    [Fact]
    public async Task ReadAsync_NotConnected_ReturnsUnavailable()
    {
        var driver = CreateDriver();
        var r = await driver.ReadAsync(Point("T", "ns=3;s=Temperature", DataType.Float));
        Assert.True(r.IsFailure);
        Assert.Equal("ResourceUnavailable", r.Error!.Code);
        Assert.Null(r.Value);
    }

    [Fact]
    public async Task ReadBatchAsync_NotConnected_ReturnsUnavailable()
    {
        var driver = CreateDriver();
        var r = await driver.ReadBatchAsync(
            [Point("A", "ns=2;i=1001", DataType.Int32), Point("B", "ns=3;s=Speed", DataType.Float)]);
        Assert.True(r.IsFailure);
        Assert.Equal("ResourceUnavailable", r.Error!.Code);
    }

    [Fact]
    public async Task WriteAsync_NotConnected_ReturnsUnavailable()
    {
        var driver = CreateDriver();
        var r = await driver.WriteAsync(Point("Set", "ns=3;s=SetPoint", DataType.Float), 42.0);
        Assert.True(r.IsFailure);
        Assert.Equal("ResourceUnavailable", r.Error!.Code);
    }

    [Fact]
    public async Task WriteBatchAsync_NotConnected_ReturnsUnavailable()
    {
        var driver = CreateDriver();
        var r = await driver.WriteBatchAsync(
            [new KeyValuePair<DevicePoint, object>(Point("A", "ns=3;s=SetPoint", DataType.Float), 1.0)]);
        Assert.True(r.IsFailure);
        Assert.Equal("ResourceUnavailable", r.Error!.Code);
    }

    [Fact]
    public async Task PingAsync_NotConnected_ReturnsUnavailable()
    {
        var driver = CreateDriver();
        var r = await driver.PingAsync();
        Assert.True(r.IsFailure);
        Assert.Equal("ResourceUnavailable", r.Error!.Code);
    }

    /// <summary>配置缺陷：端点为空 → Validation 错误（不尝试连接、不抛异常）</summary>
    [Fact]
    public async Task ConnectAsync_EmptyEndpoint_ReturnsValidation()
    {
        var driver = CreateDriver(new DeviceConnection { Endpoint = "   " });
        var r = await driver.ConnectAsync();
        Assert.True(r.IsFailure);
        Assert.Equal("ValidationError", r.Error!.Code);
        Assert.Equal(DriverState.Faulted, driver.State);
    }

    [Fact]
    public void Dispose_NotConnected_NoThrow()
    {
        var driver = CreateDriver();
        driver.Dispose();
    }
}
