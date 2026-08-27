using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NitroGateway.Command;
using NitroGateway.DeviceManagement;
using NitroGateway.Shared;
using Xunit;

namespace NitroGateway.UnitTests;

/// <summary>
/// CommandProcessor 测试（ADR-069）：幂等去重 + 写值 + 回执发布。
/// 覆盖：首写成功与回执契约（topic/JSON camelCase）、重复命令不重写值且重发同回执、
/// 写失败回执 Failure+error、写异常隔离为 Failure 回执。
/// </summary>
public sealed class CommandProcessorTests
{
    private const string SiteId = "site-a";
    private static readonly Guid DeviceId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid PointId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid CommandId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static GatewayCommand Command(object? value = null) => new()
    {
        CommandId = CommandId,
        Type = "WritePoint",
        SiteId = SiteId,
        DeviceId = DeviceId,
        PointId = PointId,
        Value = value ?? 42L,
        RequestedAt = DateTimeOffset.UtcNow
    };

    private static (CommandProcessor Processor, FakeWriteService Write, RecordingFakeMqttClient Mqtt) Make()
    {
        var write = new FakeWriteService();
        var mqtt = new RecordingFakeMqttClient();
        var processor = new CommandProcessor(write, mqtt, NullLogger<CommandProcessor>.Instance);
        return (processor, write, mqtt);
    }

    [Fact]
    public async Task ProcessAsync_FirstWrite_SuccessAndAckContract()
    {
        var (p, write, mqtt) = Make();

        await p.ProcessAsync(Command());

        Assert.Single(write.Requests);
        Assert.Equal(PointId, write.Requests[0].PointId);
        Assert.Equal(42L, write.Requests[0].Value);

        var (topic, payload) = Assert.Single(mqtt.Published);
        Assert.Equal($"nitrogateway/{SiteId}/{DeviceId}/commands/ack", topic);
        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;
        Assert.Equal(CommandId, root.GetProperty("commandId").GetGuid());
        Assert.Equal("Success", root.GetProperty("result").GetString());
        Assert.Equal("", root.GetProperty("error").GetString());
        Assert.True(root.GetProperty("at").TryGetDateTimeOffset(out _));
    }

    [Fact]
    public async Task ProcessAsync_DuplicateCommand_DoesNotRewriteAndRepublishesSameAck()
    {
        var (p, write, mqtt) = Make();

        await p.ProcessAsync(Command());
        await p.ProcessAsync(Command()); // 同一 commandId 重复投递（QoS1 重投/云侧重试）

        Assert.Single(write.Requests); // 幂等：不重复写值
        Assert.Equal(2, mqtt.Published.Count);
        Assert.Equal(mqtt.Published[0].Payload, mqtt.Published[1].Payload); // 重发同回执
    }

    [Fact]
    public async Task ProcessAsync_WriteFailure_AckFailureWithError()
    {
        var (p, write, mqtt) = Make();
        write.Handler = _ => OperationResult.Failure(OperationalError.Validation("拒绝写入"));

        await p.ProcessAsync(Command());

        using var doc = JsonDocument.Parse(mqtt.Published[0].Payload);
        var root = doc.RootElement;
        Assert.Equal("Failure", root.GetProperty("result").GetString());
        Assert.Contains("拒绝写入", root.GetProperty("error").GetString());
    }

    [Fact]
    public async Task ProcessAsync_WriteThrows_AckFailureWithMessage()
    {
        var (p, write, mqtt) = Make();
        write.Throw = new InvalidOperationException("驱动写超时");

        await p.ProcessAsync(Command());

        Assert.Single(write.Requests);
        using var doc = JsonDocument.Parse(mqtt.Published[0].Payload);
        var root = doc.RootElement;
        Assert.Equal("Failure", root.GetProperty("result").GetString());
        Assert.Contains("驱动写超时", root.GetProperty("error").GetString());
    }
}
