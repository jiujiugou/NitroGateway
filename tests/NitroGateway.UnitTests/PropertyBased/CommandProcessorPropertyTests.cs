using FsCheck.Xunit;
using Microsoft.Extensions.Logging.Abstractions;
using NitroGateway.Command;

namespace NitroGateway.UnitTests.PropertyBased;

/// <summary>命令处理器幂等的属性测试（复用 CommandTestFakes 的替身）。</summary>
public class CommandProcessorPropertyTests
{
    private static int Pos(int seed, int mod) => (int)(Math.Abs((long)seed) % mod);

    private static GatewayCommand Command(Guid commandId) => new()
    {
        CommandId = commandId,
        Type = "WritePoint",
        SiteId = "site-a",
        DeviceId = Guid.NewGuid(),
        PointId = Guid.NewGuid(),
        Value = 42L,
        RequestedAt = DateTimeOffset.UtcNow
    };

    /// <summary>性质：同一 commandId 重复投递 N 次 → 写值恰好一次，回执恰好 N 次（幂等）。</summary>
    [Property]
    public bool Same_command_writes_once_acks_each_time(int repeatSeed)
    {
        var repeats = Pos(repeatSeed, 6) + 1;   // 1..6
        var write = new FakeWriteService();
        var mqtt = new RecordingFakeMqttClient();
        var processor = new CommandProcessor(write, mqtt, NullLogger<CommandProcessor>.Instance);
        var command = Command(Guid.NewGuid());

        for (var i = 0; i < repeats; i++)
            processor.ProcessAsync(command).GetAwaiter().GetResult();

        return write.Requests.Count == 1 && mqtt.Published.Count == repeats;
    }

    /// <summary>性质：不同 commandId → 每个各写一次、各回执一次。</summary>
    [Property]
    public bool Distinct_commands_each_write_once(int countSeed)
    {
        var count = Pos(countSeed, 6) + 1;   // 1..6
        var write = new FakeWriteService();
        var mqtt = new RecordingFakeMqttClient();
        var processor = new CommandProcessor(write, mqtt, NullLogger<CommandProcessor>.Instance);

        for (var i = 0; i < count; i++)
            processor.ProcessAsync(Command(Guid.NewGuid())).GetAwaiter().GetResult();

        return write.Requests.Count == count && mqtt.Published.Count == count;
    }
}
