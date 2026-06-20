using Microsoft.Extensions.Logging;
using NitroGateway.DeviceManagement;
using NitroGateway.Domain.Devices;

namespace NitroGateway.Host;

/// <summary>首次运行时自动插入测试设备，方便对接 ModbusPal 验证</summary>
public static class DatabaseSeeder
{
    public static async Task SeedIfEmpty(IDeviceManager deviceManager, IPointManager pointManager, ILogger logger)
    {
        var devices = await deviceManager.GetAllAsync();
        if (devices.IsSuccess && devices.Value!.Count > 0)
        {
            logger.LogInformation("已有 {Count} 台设备，跳过种子数据", devices.Value.Count);
            return;
        }

        logger.LogInformation("未检测到设备，插入测试数据...");

        var device = new Device
        {
            Id = Guid.NewGuid(),
            Name = "PLC-Test",
            Description = "ModbusPal 模拟设备 (127.0.0.1:502)",
            Protocol = ProtocolIdentifier.Modbus,
            Connection = new DeviceConnection
            {
                Endpoint = "127.0.0.1:502",
                ConnectTimeoutMs = 3000,
                RequestTimeoutMs = 5000,
                Parameters = new Dictionary<string, object>
                {
                    ["UnitId"] = 1,
                    ["Endian"] = "ABCD"
                }
            },
            Status = DeviceStatus.Online
        };

        var regResult = await deviceManager.RegisterAsync(device);
        if (regResult.IsFailure)
        {
            logger.LogError("设备注册失败: {Error}", regResult.Error!.Message);
            return;
        }

        var points = new[]
        {
            new DevicePoint { Id = Guid.NewGuid(), Name = "Temperature", Address = "40001", DataType = DataType.Float,  ScaleFactor = 1.0, Description = "温度 (Float, 2 regs)" },
            new DevicePoint { Id = Guid.NewGuid(), Name = "Pressure",    Address = "40003", DataType = DataType.Int16, ScaleFactor = 0.1, Description = "压力 (Int16, 1 reg, ×0.1)" },
            new DevicePoint { Id = Guid.NewGuid(), Name = "FlowRate",    Address = "40004", DataType = DataType.Int32, ScaleFactor = 0.01, Description = "流量 (Int32, 2 regs, ×0.01)" },
            new DevicePoint { Id = Guid.NewGuid(), Name = "Running",     Address = "00001", DataType = DataType.Bool,  Description = "运行状态 (Coil 1)" },
        };

        foreach (var pt in points)
            await pointManager.AddAsync(device.Id, pt);

        logger.LogInformation("已插入测试设备: {Name} ({Id}), {Count} 个点位", device.Name, device.Id, points.Length);
    }
}
