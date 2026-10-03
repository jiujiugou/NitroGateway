using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NitroGateway.Alarm.Hosted;
using NitroGateway.Alarm.Notification;
using NitroGateway.Alarm.Repository;
using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Events;
using Xunit;
using AlarmDomain = NitroGateway.Alarm.Domain;

namespace NitroGateway.UnitTests.Alarms;

/// <summary>告警后台服务：消费 PointStoredEvent → 评估 → 落库 + 通知（含恢复路径）。</summary>
public class AlarmHostedServiceTests
{
    private sealed class CapturingNotifier : IAlarmNotifier
    {
        public string Name => "capture";
        public List<AlarmDomain.Alarm> Notified { get; } = [];

        public Task NotifyAsync(AlarmDomain.Alarm alarm, CancellationToken ct = default)
        {
            lock (Notified) Notified.Add(alarm);
            return Task.CompletedTask;
        }
    }

    private static AlarmDomain.AlarmRule Rule(Guid deviceId, Guid pointId, double threshold) => new()
    {
        Id = Guid.NewGuid(),
        DeviceId = deviceId,
        PointId = pointId,
        Operator = ">",
        Threshold = threshold,
        DurationSeconds = 0,
        Severity = AlarmDomain.AlarmSeverity.Critical,
        Enabled = true
    };

    private static PointStoredEvent Event(Guid deviceId, Guid pointId, double value) => new()
    {
        DeviceId = deviceId,
        Snapshots =
        [
            new PointSnapshot
            {
                DeviceId = deviceId,
                DevicePointId = pointId,
                DataType = DataType.Float,
                Value = value,
                Quality = QualityCode.Good,
                Timestamp = DateTime.UtcNow
            }
        ]
    };

    private static async Task<(AlarmHostedService Service, InMemoryAlarmRepository Alarms, ServiceProvider Provider)> StartAsync(
        InMemoryAlarmRuleRepository rules, CapturingNotifier notifier)
    {
        var alarmRepo = new InMemoryAlarmRepository();
        var services = new ServiceCollection();
        services.AddSingleton<IAlarmRuleRepository>(rules);
        services.AddSingleton<IAlarmRepository>(alarmRepo);
        services.AddSingleton<IAlarmNotifier>(notifier);
        var provider = services.BuildServiceProvider();

        var service = new AlarmHostedService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<AlarmHostedService>.Instance);
        await service.StartAsync(CancellationToken.None);
        return (service, alarmRepo, provider);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("等待告警处理超时");
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task ExceededRule_SavesActiveAlarmAndNotifies()
    {
        var deviceId = Guid.NewGuid();
        var pointId = Guid.NewGuid();
        var rules = new InMemoryAlarmRuleRepository();
        await rules.SaveAsync(Rule(deviceId, pointId, 80));
        var notifier = new CapturingNotifier();

        var (service, alarmRepo, provider) = await StartAsync(rules, notifier);
        try
        {
            await service.OnStoredAsync(Event(deviceId, pointId, 85), CancellationToken.None);

            await WaitUntilAsync(() => notifier.Notified.Count >= 1);
            Assert.Equal(85, Assert.Single(notifier.Notified).TriggerValue);
            Assert.Single((await alarmRepo.GetAllActiveAsync()).Value!);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            await provider.DisposeAsync();
        }
    }

    [Fact]
    public async Task NormalValueAfterActive_ResolvesAlarm()
    {
        var deviceId = Guid.NewGuid();
        var pointId = Guid.NewGuid();
        var rules = new InMemoryAlarmRuleRepository();
        await rules.SaveAsync(Rule(deviceId, pointId, 80));
        var notifier = new CapturingNotifier();

        var (service, alarmRepo, provider) = await StartAsync(rules, notifier);
        try
        {
            await service.OnStoredAsync(Event(deviceId, pointId, 85), CancellationToken.None);
            await WaitUntilAsync(() => notifier.Notified.Count >= 1);

            await service.OnStoredAsync(Event(deviceId, pointId, 70), CancellationToken.None);

            await WaitUntilAsync(() => alarmRepo.GetAllActiveAsync().GetAwaiter().GetResult().Value!.Count == 0);
            Assert.Empty((await alarmRepo.GetAllActiveAsync()).Value!);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            await provider.DisposeAsync();
        }
    }

    [Fact]
    public async Task NoMatchingRule_DoesNothing()
    {
        var deviceId = Guid.NewGuid();
        var rules = new InMemoryAlarmRuleRepository(); // 无规则
        var notifier = new CapturingNotifier();

        var (service, alarmRepo, provider) = await StartAsync(rules, notifier);
        try
        {
            await service.OnStoredAsync(Event(deviceId, Guid.NewGuid(), 999), CancellationToken.None);
            await Task.Delay(200);

            Assert.Empty(notifier.Notified);
            Assert.Empty((await alarmRepo.GetAllActiveAsync()).Value!);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            await provider.DisposeAsync();
        }
    }
}
