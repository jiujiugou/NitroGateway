using NitroGateway.Domain.Devices;

namespace NitroGateway.LoadTests;

/// <summary>生成压测用设备与点位（内存态，不落库）。</summary>
public static class PointGenerator
{
    private static readonly DataType[] Types =
    [
        DataType.Float, DataType.Int32, DataType.UInt32, DataType.Int16,
        DataType.UInt16, DataType.Int64, DataType.UInt64, DataType.Double, DataType.Bool
    ];

    /// <summary>生成 <paramref name="deviceCount"/> 台设备，每台 <paramref name="pointsPerDevice"/> 个点位。</summary>
    public static List<Device> Generate(int deviceCount, int pointsPerDevice)
    {
        var devices = new List<Device>(deviceCount);
        for (var d = 0; d < deviceCount; d++)
        {
            var device = new Device
            {
                Id = Guid.NewGuid(),
                Name = $"LoadDev-{d:D4}",
                Protocol = ProtocolIdentifier.Modbus,
                Connection = new DeviceConnection { Endpoint = $"sim://load/{d}" },
                Status = DeviceStatus.Online
            };

            for (var p = 0; p < pointsPerDevice; p++)
            {
                var type = Types[p % Types.Length];
                device.AddPoint(new DevicePoint
                {
                    Id = Guid.NewGuid(),
                    Name = $"P{p:D4}",
                    Address = $"{40001 + p * 2}",
                    DataType = type,
                    Enabled = true,
                    // Deadband=0 → 每样本都放行，压满落库与 Channel（避免被变化抑制隐藏负载）
                    Deadband = 0,
                    ScaleFactor = type == DataType.Int16 ? 0.1 : 1.0
                });
            }

            devices.Add(device);
        }

        return devices;
    }
}
