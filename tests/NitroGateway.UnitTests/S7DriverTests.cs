using HslCommunication.Profinet.Siemens;
using Microsoft.Extensions.Logging.Abstractions;
using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Protocols;
using NitroGateway.Protocols.S7;
using Xunit;

namespace NitroGateway.UnitTests;

/// <summary>
/// S7Driver 测试（ADR-019 P1-1/P2-2/P3-1/P3-2）：
/// 注入未连接的 SiemensS7Net 客户端，验证失败读返回 Failure 而非默认值 0、全失败返回 Failure 并复位 Faulted。
/// </summary>
public class S7DriverTests
{
    private static S7Driver CreateDriver(DeviceConnection? connection = null)
    {
        var conn = connection ?? new DeviceConnection { Endpoint = "127.0.0.1:102" };
        // 未 ConnectServer 的客户端：Hsl 读操作返回失败或抛异常，正好验证"失败读不产出伪值"
        var client = new SiemensS7Net(SiemensPLCS.S1200) { IpAddress = "127.0.0.1", Port = 102 };
        return new S7Driver(conn, NullLogger.Instance, client);
    }

    private static DevicePoint Point(string name, string address, DataType type) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Address = address,
        DataType = type
    };

    /// <summary>ADR-019 P1-1：失败读返回 Failure，绝不把故障当作 0.0 产出</summary>
    [Fact]
    public async Task ReadAsync_ClientNotConnected_ReturnsFailure_NotFakeZero()
    {
        var driver = CreateDriver();
        var r = await driver.ReadAsync(Point("T", "DB1.DBD0", DataType.Float));
        Assert.True(r.IsFailure, "失败读应返回 Failure");
        Assert.Null(r.Value);
    }

    /// <summary>ADR-019 P2-2：各 DataType 均走类型映射读路径，失败时统一 Failure（覆盖 switch 全分支）</summary>
    [Theory]
    [InlineData(DataType.Bool, "DB1.DBX0.0")]
    [InlineData(DataType.Byte, "DB1.DBB0")]
    [InlineData(DataType.Int16, "DB1.DBW0")]
    [InlineData(DataType.UInt16, "DB1.DBW0")]
    [InlineData(DataType.Int32, "DB1.DBD0")]
    [InlineData(DataType.UInt32, "DB1.DBD0")]
    [InlineData(DataType.Int64, "DB1.DBD0")]
    [InlineData(DataType.UInt64, "DB1.DBD0")]
    [InlineData(DataType.Float, "DB1.DBD0")]
    [InlineData(DataType.Double, "DB1.DBD0")]
    [InlineData(DataType.String, "DB1.DBB0")]
    public async Task ReadAsync_AllTypes_NotConnected_ReturnsFailure(DataType type, string address)
    {
        var driver = CreateDriver();
        var r = await driver.ReadAsync(Point(type.ToString(), address, type));
        Assert.True(r.IsFailure, $"{type} 失败读应返回 Failure 而非默认值");
        Assert.Null(r.Value);
    }

    /// <summary>ADR-019 P3-1：全失败返回 Failure 并复位 Faulted（与 Modbus 对齐），不再空成功</summary>
    [Fact]
    public async Task ReadBatchAsync_AllPointsFail_ReturnsFailureAndFaulted()
    {
        var driver = CreateDriver();
        var r = await driver.ReadBatchAsync(
            [Point("A", "DB1.DBD0", DataType.Float), Point("B", "DB1.DBD4", DataType.Float)]);

        Assert.True(r.IsFailure, "全失败应返回 Failure");
        Assert.Equal(DriverState.Faulted, driver.State);
    }

    /// <summary>空点位列表直接成功，不视为错误（与 Modbus 一致）</summary>
    [Fact]
    public async Task ReadBatchAsync_EmptyPoints_ReturnsEmptySuccess()
    {
        var driver = CreateDriver();
        var r = await driver.ReadBatchAsync([]);
        Assert.True(r.IsSuccess);
        Assert.Empty(r.Value!);
    }

    /// <summary>ADR-019 P3-2：未连接时 Ping 返回 Failure 不抛异常（ping 地址可配置路径不依赖 DB1）</summary>
    [Fact]
    public async Task PingAsync_ClientNotConnected_ReturnsFailure_NotThrow()
    {
        var driver = CreateDriver();
        var r = await driver.PingAsync();
        Assert.True(r.IsFailure);
    }

    /// <summary>未连接时写返回不可用失败</summary>
    [Fact]
    public async Task WriteAsync_ClientNotConnected_ReturnsFailure()
    {
        var driver = CreateDriver();
        var r = await driver.WriteAsync(Point("T", "DB1.DBD0", DataType.Float), 1.5f);
        Assert.True(r.IsFailure);
    }

    /// <summary>M 区点位（ADR-019 P2-3）失败读同样返回 Failure 而非默认值</summary>
    [Fact]
    public async Task ReadAsync_MemoryArea_NotConnected_ReturnsFailure()
    {
        var driver = CreateDriver();
        var r = await driver.ReadAsync(Point("M1", "M100", DataType.Float));
        Assert.True(r.IsFailure, "M 区失败读应返回 Failure");
    }

    // ══════════════════════════════════════════════════
    //  CpuType 解析（ADR-024 P1-1：默认值不再抛 SwitchExpressionException，未知型号显式报错）
    // ══════════════════════════════════════════════════

    /// <summary>红绿对照：修复前默认 "S71200" 不在 switch 分支，无 default 抛 SwitchExpressionException</summary>
    [Fact]
    public void ParseCpuType_Default_ReturnsS1200()
    {
        Assert.Equal(SiemensPLCS.S1200, S7Driver.ParseCpuType(null));
        Assert.Equal(SiemensPLCS.S1200, S7Driver.ParseCpuType(""));
        Assert.Equal(SiemensPLCS.S1200, S7Driver.ParseCpuType("S-1200"));
    }

    [Theory]
    [InlineData("S-1500", SiemensPLCS.S1500)]
    [InlineData("S-300", SiemensPLCS.S300)]
    [InlineData("S-400", SiemensPLCS.S400)]
    public void ParseCpuType_KnownTypes_Map(string raw, SiemensPLCS expected)
    {
        Assert.Equal(expected, S7Driver.ParseCpuType(raw));
    }

    /// <summary>未知 CpuType 显式报错（ADR-024 P2-1），不再静默默认为 S1200</summary>
    [Fact]
    public void ParseCpuType_Unknown_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() => S7Driver.ParseCpuType("S-200"));
        Assert.Contains("未知的 S7 CpuType", ex.Message);
    }

    /// <summary>地址类型与 DataType 冲突时读返回 Failure（携带明确原因），不静默读错字节</summary>
    [Fact]
    public async Task ReadAsync_AddressTypeConflict_ReturnsFailureWithReason()
    {
        var driver = CreateDriver();
        var r = await driver.ReadAsync(Point("T", "MW10", DataType.Float));
        Assert.True(r.IsFailure, "地址类型冲突应返回 Failure");
        Assert.Contains("不兼容", r.Error?.Message ?? "");
    }
}

