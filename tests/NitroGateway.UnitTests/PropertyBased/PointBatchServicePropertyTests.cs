using System.Globalization;
using FsCheck.Xunit;
using Microsoft.Extensions.Logging.Abstractions;
using NitroGateway.DeviceManagement;
using NitroGateway.Domain.Devices;

namespace NitroGateway.UnitTests.PropertyBased;

/// <summary>点位批量服务的属性测试：CSV 导出∘导入 往返、地址递增单调。</summary>
public class PointBatchServicePropertyTests
{
    private readonly PointBatchService _service = new(NullLogger<PointBatchService>.Instance);

    private static int Pos(int seed, int mod) => (int)(Math.Abs((long)seed) % mod);

    /// <summary>生成一个不以外层空白开头/结尾、但内部可能含逗号/引号/空格的名称（触发 CSV 转义）。</summary>
    private static string Name(System.Random rnd)
    {
        const string alphabet = "aZ,\" ";
        var len = rnd.Next(0, 5);
        var middle = new char[len];
        for (var i = 0; i < len; i++)
            middle[i] = alphabet[rnd.Next(alphabet.Length)];
        return "a" + new string(middle) + "z";
    }

    /// <summary>性质：ExportCsv → ParseCsv 往返保留各字段（含含逗号/引号的名称）。</summary>
    [Property]
    public bool Csv_export_parse_roundtrip_preserves_fields(int seed)
    {
        var rnd = new System.Random(seed);
        var count = rnd.Next(1, 4);
        var points = new List<DevicePoint>();
        for (var i = 0; i < count; i++)
        {
            points.Add(new DevicePoint
            {
                Id = Guid.NewGuid(),
                Name = Name(rnd),
                Address = "4" + (10000 + i),
                DataType = DataType.Int16,
                Access = PointAccess.ReadWrite,
                Enabled = true,
                ScanIntervalMs = 1000,
                Deadband = 0.5,
                ScaleFactor = 0.1,
                ScaleOffset = 5,
                Description = i == 0 ? "" : Name(rnd)
            });
        }

        var csv = _service.ExportCsv(points);
        var parsed = _service.ParseCsv(csv);
        if (parsed.IsFailure) return false;
        var back = parsed.Value!;
        if (back.Count != points.Count) return false;

        for (var i = 0; i < points.Count; i++)
        {
            if (back[i].Name != points[i].Name) return false;
            if (back[i].Address != points[i].Address) return false;
            if (back[i].DataType != points[i].DataType) return false;
            if (back[i].Access != points[i].Access) return false;
            if (back[i].Enabled != points[i].Enabled) return false;
            if (back[i].ScanIntervalMs != points[i].ScanIntervalMs) return false;
            if (Math.Abs(back[i].Deadband - points[i].Deadband) > 1e-12) return false;
            if (Math.Abs(back[i].ScaleFactor - points[i].ScaleFactor) > 1e-12) return false;
            if (Math.Abs(back[i].ScaleOffset - points[i].ScaleOffset) > 1e-12) return false;
        }

        return true;
    }

    /// <summary>性质：Modbus 批量生成的地址严格递增。</summary>
    [Property]
    public bool Generate_modbus_addresses_strictly_increase(int countSeed, bool useFloat)
    {
        var count = Pos(countSeed, 20) + 1;   // 1..20
        var type = useFloat ? DataType.Float : DataType.Int16;

        var points = _service.Generate(Guid.NewGuid(), "p_{###}", "40001", count, type);
        if (points.Count != count) return false;

        var addresses = points.Select(p => int.Parse(p.Address, CultureInfo.InvariantCulture)).ToList();
        for (var i = 1; i < addresses.Count; i++)
        {
            if (addresses[i] <= addresses[i - 1]) return false;
        }

        return true;
    }
}
