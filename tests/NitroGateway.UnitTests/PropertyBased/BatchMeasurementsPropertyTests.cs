using System.Text;
using System.Text.Json;
using FsCheck.Xunit;
using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Measurements;
using NitroGateway.Forwarder;

namespace NitroGateway.UnitTests.PropertyBased;

/// <summary>上行载荷 BatchMeasurements 的 JSON 往返属性测试。</summary>
public class BatchMeasurementsPropertyTests
{
    // 与生产反序列化（SqliteForwardOutbox 的 _json）一致的 camelCase 选项
    private static readonly JsonSerializerOptions Camel = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static BatchMeasurements Build(System.Random rnd)
    {
        var deviceId = Guid.NewGuid();
        var scanStart = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(rnd.Next(0, 1_000_000));
        var records = new List<MeasurementRecord>();
        var count = rnd.Next(0, 5);
        for (var i = 0; i < count; i++)
        {
            records.Add(new MeasurementRecord
            {
                Id = Guid.NewGuid(),
                DeviceId = deviceId,
                DevicePointId = Guid.NewGuid(),
                PointName = "p" + i,
                Value = (i % 4) switch { 0 => (object)12.5, 1 => 42, 2 => true, _ => "txt" },
                DataType = DataType.Float,
                Timestamp = scanStart.AddMilliseconds(i),
                ReceivedAt = scanStart.AddMilliseconds(i + 1),
                Quality = QualityCode.Good
            });
        }

        return new BatchMeasurements
        {
            Id = Guid.NewGuid(),
            DeviceId = deviceId,
            SiteId = "site-1",
            V = 1,
            ScanStartedAt = scanStart,
            ScanCompletedAt = scanStart.AddSeconds(1),
            Records = records
        };
    }

    /// <summary>性质：序列化 → 反序列化 → 再序列化，字节完全一致（载荷稳定）。</summary>
    [Property]
    public bool Serialization_is_stable_across_roundtrip(int seed)
    {
        var batch = Build(new System.Random(seed));
        var serializer = new JsonMessageSerializer();

        var bytes1 = serializer.Serialize(batch);
        var back = JsonSerializer.Deserialize<BatchMeasurements>(Encoding.UTF8.GetString(bytes1), Camel)!;
        var bytes2 = serializer.Serialize(back);

        return bytes1.SequenceEqual(bytes2);
    }

    /// <summary>性质：往返保留批次标识与记录条数。</summary>
    [Property]
    public bool Roundtrip_preserves_identity(int seed)
    {
        var batch = Build(new System.Random(seed));
        var bytes = new JsonMessageSerializer().Serialize(batch);
        var back = JsonSerializer.Deserialize<BatchMeasurements>(Encoding.UTF8.GetString(bytes), Camel)!;

        return back.Id == batch.Id
            && back.DeviceId == batch.DeviceId
            && back.SiteId == batch.SiteId
            && back.V == batch.V
            && back.Records.Count == batch.Records.Count;
    }
}
