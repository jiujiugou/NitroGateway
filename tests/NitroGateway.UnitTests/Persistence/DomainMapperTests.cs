using NitroGateway.Domain.Devices;
using NitroGateway.Persistence;
using Xunit;

namespace NitroGateway.UnitTests.Persistence;

/// <summary>
/// DomainMapper：EF 实体 ↔ 领域模型双向映射、UpdatedAt 编解码、连接参数 JSON、枚举解析。
/// 纯静态函数，无需数据库。
/// </summary>
public class DomainMapperTests
{
    private static DeviceEntity NewEntity() => new()
    {
        Id = Guid.NewGuid(),
        Name = "PLC-1",
        Description = "desc",
        ProtocolName = "Modbus",
        ProtocolDialect = "TCP",
        Endpoint = "127.0.0.1:502",
        ConnectTimeoutMs = 1111,
        RequestTimeoutMs = 2222,
        RetryCount = 4,
        Status = "Online",
        ConnectionParams = "{\"unitId\":1}",
        UpdatedAt = "2024-01-02T03:04:05.0000000Z",
        IsDeleted = true,
        SiteId = "site-1"
    };

    private static Device NewDevice() => new()
    {
        Id = Guid.NewGuid(),
        Name = "PLC-1",
        Description = "desc",
        Protocol = new ProtocolIdentifier { Name = "Modbus", Dialect = "TCP" },
        Connection = new DeviceConnection
        {
            Endpoint = "127.0.0.1:502",
            ConnectTimeoutMs = 1111,
            RequestTimeoutMs = 2222,
            RetryCount = 4,
            Parameters = new Dictionary<string, object> { ["unitId"] = 1 }
        },
        Status = DeviceStatus.Online,
        UpdatedAt = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc),
        IsDeleted = true,
        SiteId = "site-1"
    };

    // ── Device 映射 ──

    [Fact]
    public void ToDomain_Device_MapsAllFields()
    {
        var entity = NewEntity();

        var device = DomainMapper.ToDomain(entity);

        Assert.Equal(entity.Id, device.Id);
        Assert.Equal("PLC-1", device.Name);
        Assert.Equal("desc", device.Description);
        Assert.Equal("Modbus", device.Protocol.Name);
        Assert.Equal("TCP", device.Protocol.Dialect);
        Assert.Equal("127.0.0.1:502", device.Connection.Endpoint);
        Assert.Equal(1111, device.Connection.ConnectTimeoutMs);
        Assert.Equal(2222, device.Connection.RequestTimeoutMs);
        Assert.Equal(4, device.Connection.RetryCount);
        Assert.True(device.Connection.Parameters.ContainsKey("unitId"));
        Assert.Equal(DeviceStatus.Online, device.Status);
        Assert.Equal(new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc), device.UpdatedAt);
        Assert.True(device.IsDeleted);
        Assert.Equal("site-1", device.SiteId);
    }

    [Fact]
    public void ToDomain_NullSiteIdAndParams_ReturnsDefaults()
    {
        var entity = NewEntity();
        entity.SiteId = null!;
        entity.ConnectionParams = null;

        var device = DomainMapper.ToDomain(entity);

        Assert.Equal("", device.SiteId);
        Assert.Empty(device.Connection.Parameters);
    }

    [Fact]
    public void ToDomain_NullJsonParams_ReturnsEmpty()
    {
        var entity = NewEntity();
        entity.ConnectionParams = "null";   // JSON null → Deserialize 返回 null

        var device = DomainMapper.ToDomain(entity);

        Assert.Empty(device.Connection.Parameters);
    }

    [Fact]
    public void ToEntity_Device_MapsAllFields()
    {
        var device = NewDevice();

        var entity = DomainMapper.ToEntity(device);

        Assert.Equal(device.Id, entity.Id);
        Assert.Equal("PLC-1", entity.Name);
        Assert.Equal("desc", entity.Description);
        Assert.Equal("Modbus", entity.ProtocolName);
        Assert.Equal("TCP", entity.ProtocolDialect);
        Assert.Equal("127.0.0.1:502", entity.Endpoint);
        Assert.Equal(1111, entity.ConnectTimeoutMs);
        Assert.Equal(2222, entity.RequestTimeoutMs);
        Assert.Equal(4, entity.RetryCount);
        Assert.Equal("Online", entity.Status);
        Assert.Contains("unitId", entity.ConnectionParams);
        Assert.Equal("2024-01-02T03:04:05.0000000Z", entity.UpdatedAt);
        Assert.True(entity.IsDeleted);
        Assert.Equal("site-1", entity.SiteId);
    }

    [Fact]
    public void ToEntity_NullSiteId_BecomesEmpty()
    {
        var device = NewDevice();
        device.SiteId = null!;

        var entity = DomainMapper.ToEntity(device);

        Assert.Equal("", entity.SiteId);
    }

    [Fact]
    public void ToEntity_EmptyParameters_SerializesEmptyObject()
    {
        var device = NewDevice();
        device.Connection.Parameters.Clear();

        var entity = DomainMapper.ToEntity(device);

        Assert.Equal("{}", entity.ConnectionParams);
    }

    [Fact]
    public void ToEntity_NullParameters_SerializesEmptyObject()
    {
        var device = new Device
        {
            Id = Guid.NewGuid(),
            Name = "PLC-1",
            Protocol = new ProtocolIdentifier { Name = "Modbus", Dialect = "TCP" },
            Connection = new DeviceConnection { Endpoint = "127.0.0.1:502", Parameters = null! },
            Status = DeviceStatus.Online
        };

        var entity = DomainMapper.ToEntity(device);

        Assert.Equal("{}", entity.ConnectionParams);
    }

    [Fact]
    public void ToEntity_ParameterTransform_IsApplied()
    {
        var device = NewDevice();
        var called = false;

        var entity = DomainMapper.ToEntity(device, parameters =>
        {
            called = true;
            var copy = new Dictionary<string, object>(parameters) { ["encrypted"] = "yes" };
            return copy;
        });

        Assert.True(called);
        Assert.Contains("encrypted", entity.ConnectionParams);
    }

    // ── Point 映射 ──

    [Fact]
    public void ToDomain_Point_MapsAllFields()
    {
        var entity = new PointEntity
        {
            Id = Guid.NewGuid(),
            DeviceId = Guid.NewGuid(),
            Name = "Temp",
            Address = "40001",
            Description = "炉温",
            DataType = "Float",
            Access = "ReadWrite",
            Enabled = false,
            ScanIntervalMs = 500,
            Deadband = 1.5,
            ScaleFactor = 2.5,
            ScaleOffset = 3.5,
            MinLimit = -10,
            MaxLimit = 10,
            UpdatedAt = "2024-01-02T03:04:05.0000000Z",
            IsDeleted = true
        };

        var point = DomainMapper.ToDomain(entity);

        Assert.Equal(entity.Id, point.Id);
        Assert.Equal("Temp", point.Name);
        Assert.Equal("40001", point.Address);
        Assert.Equal("炉温", point.Description);
        Assert.Equal(DataType.Float, point.DataType);
        Assert.Equal(PointAccess.ReadWrite, point.Access);
        Assert.False(point.Enabled);
        Assert.Equal(500, point.ScanIntervalMs);
        Assert.Equal(1.5, point.Deadband);
        Assert.Equal(2.5, point.ScaleFactor);
        Assert.Equal(3.5, point.ScaleOffset);
        Assert.Equal(-10, point.MinLimit);
        Assert.Equal(10, point.MaxLimit);
        Assert.True(point.IsDeleted);
    }

    [Fact]
    public void ToEntity_Point_MapsAllFields()
    {
        var deviceId = Guid.NewGuid();
        var point = new DevicePoint
        {
            Id = Guid.NewGuid(),
            Name = "Temp",
            Address = "40001",
            Description = "炉温",
            DataType = DataType.Int16,
            Access = PointAccess.ReadWrite,
            Enabled = false,
            ScanIntervalMs = 500,
            Deadband = 1.5,
            ScaleFactor = 2.5,
            ScaleOffset = 3.5,
            MinLimit = -10,
            MaxLimit = 10,
            UpdatedAt = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            IsDeleted = true
        };

        var entity = DomainMapper.ToEntity(point, deviceId);

        Assert.Equal(point.Id, entity.Id);
        Assert.Equal(deviceId, entity.DeviceId);
        Assert.Equal("Temp", entity.Name);
        Assert.Equal("40001", entity.Address);
        Assert.Equal("炉温", entity.Description);
        Assert.Equal("Int16", entity.DataType);
        Assert.Equal("ReadWrite", entity.Access);
        Assert.False(entity.Enabled);
        Assert.Equal(500, entity.ScanIntervalMs);
        Assert.Equal(1.5, entity.Deadband);
        Assert.Equal(2.5, entity.ScaleFactor);
        Assert.Equal(3.5, entity.ScaleOffset);
        Assert.Equal(-10, entity.MinLimit);
        Assert.Equal(10, entity.MaxLimit);
        Assert.Equal("2024-01-02T03:04:05.0000000Z", entity.UpdatedAt);
        Assert.True(entity.IsDeleted);
    }

    // ── UpdatedAt 编解码 ──

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ParseUpdatedAt_Empty_ReturnsMinValue(string? value)
        => Assert.Equal(DateTime.MinValue, DomainMapper.ParseUpdatedAt(value));

    [Fact]
    public void ParseUpdatedAt_Invalid_ReturnsMinValue()
        => Assert.Equal(DateTime.MinValue, DomainMapper.ParseUpdatedAt("not-a-date"));

    [Fact]
    public void ParseUpdatedAt_ValidUtc_ParsesToUtc()
    {
        var parsed = DomainMapper.ParseUpdatedAt("2024-01-02T03:04:05.0000000Z");

        Assert.Equal(DateTimeKind.Utc, parsed.Kind);
        Assert.Equal(new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc), parsed);
    }

    [Fact]
    public void FormatUpdatedAt_MinValue_ReturnsEmpty()
        => Assert.Equal("", DomainMapper.FormatUpdatedAt(DateTime.MinValue));

    [Fact]
    public void FormatUpdatedAt_Utc_ReturnsOFormat()
        => Assert.Equal("2024-01-02T03:04:05.0000000Z",
            DomainMapper.FormatUpdatedAt(new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc)));

    [Fact]
    public void FormatUpdatedAt_RoundTripsWithParse()
    {
        var value = new DateTime(2024, 6, 7, 8, 9, 10, DateTimeKind.Utc);
        Assert.Equal(value, DomainMapper.ParseUpdatedAt(DomainMapper.FormatUpdatedAt(value)));
    }

    // ── ParseEnum ──

    [Fact]
    public void ParseEnum_IgnoreCase()
    {
        Assert.Equal(DeviceStatus.Online, DomainMapper.ParseEnum<DeviceStatus>("online"));
        Assert.Equal(DeviceStatus.Online, DomainMapper.ParseEnum<DeviceStatus>("ONLINE"));
    }

    [Fact]
    public void ParseEnum_Invalid_ReturnsDefault()
    {
        Assert.Equal(default, DomainMapper.ParseEnum<DeviceStatus>("bogus"));
        Assert.Equal(default, DomainMapper.ParseEnum<DeviceStatus>(null));
    }
}
