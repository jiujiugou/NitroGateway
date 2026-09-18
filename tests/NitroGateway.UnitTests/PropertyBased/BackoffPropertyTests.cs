using FsCheck.Xunit;
using NitroGateway.Transport.MQTT;

namespace NitroGateway.UnitTests.PropertyBased;

/// <summary>MQTT 重连退避计算的属性测试。</summary>
public class BackoffPropertyTests
{
    /// <summary>
    /// 性质：对任意基数 / 重试次数 / 上限，退避延迟恒为非负且不超过上限。
    /// <para>该性质暴露了"指数在 n≈23 时 int 溢出为负 → Task.Delay(负数) 抛异常"的问题。</para>
    /// </summary>
    [Property]
    public bool Delay_is_bounded_and_non_negative(int baseSeed, int attemptSeed, int maxSeed)
    {
        var baseMs = Math.Clamp((int)(Math.Abs((long)baseSeed) % 1_000_000) + 1, 1, 1_000_000);
        var attempt = (int)(Math.Abs((long)attemptSeed) % 1000) + 1;
        var maxMs = Math.Clamp((int)(Math.Abs((long)maxSeed) % 1_000_000) + 1, 1, 1_000_000);

        var delay = MqttClientWrapper.ComputeBackoffDelayMs(baseMs, attempt, maxMs);

        return delay >= 0 && delay <= maxMs;
    }
}
