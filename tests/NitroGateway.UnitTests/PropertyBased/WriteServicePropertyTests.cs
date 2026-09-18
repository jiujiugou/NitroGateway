using FsCheck;
using FsCheck.Xunit;
using NitroGateway.DeviceManagement;
using NitroGateway.Domain.Devices;
using Xunit;

namespace NitroGateway.UnitTests.PropertyBased;

/// <summary>写服务值转换 / 反向缩放的属性测试。</summary>
public class WriteServicePropertyTests
{
    private static readonly DataType[] AllTypes = Enum.GetValues<DataType>();

    private static int Pos(int seed, int mod) => (int)(Math.Abs((long)seed) % mod);

    /// <summary>性质：ToRawValue 是正向缩放（raw × scale + offset）的逆运算。</summary>
    [Property]
    public bool ToRawValue_inverts_forward_scaling(int valueSeed, int scaleSeed, int offsetSeed)
    {
        double engineering = Pos(valueSeed, 1_000_000);
        var scale = Math.Clamp(Pos(scaleSeed, 1000), 1, 1000) / 100.0;   // 0.01 .. 10.00（非零）
        double offset = Pos(offsetSeed, 1000) - 500;

        var point = new DevicePoint
        {
            Name = "p",
            Address = "40001",
            DataType = DataType.Double,
            ScaleFactor = scale,
            ScaleOffset = offset
        };

        var raw = Convert.ToDouble(WriteService.ToRawValue(point, engineering), System.Globalization.CultureInfo.InvariantCulture);
        var forward = raw * scale + offset;

        return Math.Abs(forward - engineering) <= 1e-6 * Math.Max(1.0, Math.Abs(engineering));
    }

    /// <summary>性质：ConvertValue 对任意输入都不抛异常——失败以 OperationResult.Failure 表达。</summary>
    [Property]
    public bool ConvertValue_never_throws(int typeSeed, string value)
    {
        var type = AllTypes[Pos(typeSeed, AllTypes.Length)];
        try
        {
            WriteService.ConvertValue(type, value);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>性质：数值类型的合法整数输入恒被正确转换（Int32 恒等）。</summary>
    [Property]
    public bool ConvertValue_int32_identity(int v)
    {
        var result = WriteService.ConvertValue(DataType.Int32, v);
        return result.IsSuccess && result.Value is int i && i == v;
    }
}
