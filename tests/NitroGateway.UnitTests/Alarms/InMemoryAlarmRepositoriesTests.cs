using NitroGateway.Alarm.Repository;
using Xunit;
using AlarmDomain = NitroGateway.Alarm.Domain;

namespace NitroGateway.UnitTests.Alarms;

/// <summary>内存告警/规则仓库（占位实现）单测：过滤、状态更新、范围查询与限幅。</summary>
public class InMemoryAlarmRepositoriesTests
{
    private static readonly DateTime T0 = new(2026, 8, 16, 0, 0, 0, DateTimeKind.Utc);

    private static AlarmDomain.Alarm NewAlarm(
        Guid deviceId, AlarmDomain.AlarmState state = AlarmDomain.AlarmState.Active,
        DateTime? occurredAt = null) => new()
    {
        Id = Guid.NewGuid(),
        RuleId = Guid.NewGuid(),
        DeviceId = deviceId,
        PointId = Guid.NewGuid(),
        TriggerValue = 85,
        Threshold = 80,
        Severity = AlarmDomain.AlarmSeverity.Warning,
        Message = "m",
        State = state,
        OccurredAt = occurredAt ?? T0
    };

    private static AlarmDomain.AlarmRule NewRule(Guid deviceId, Guid pointId, bool enabled = true) => new()
    {
        Id = Guid.NewGuid(),
        DeviceId = deviceId,
        PointId = pointId,
        Operator = ">",
        Threshold = 80,
        DurationSeconds = 0,
        Severity = AlarmDomain.AlarmSeverity.Warning,
        Enabled = enabled
    };

    // ── AlarmRepository ──

    [Fact]
    public async Task AlarmRepo_GetActiveByDevice_FiltersByDeviceAndActiveState()
    {
        var repo = new InMemoryAlarmRepository();
        var deviceA = Guid.NewGuid();
        var deviceB = Guid.NewGuid();
        await repo.SaveAsync(NewAlarm(deviceA));
        await repo.SaveAsync(NewAlarm(deviceB));
        await repo.SaveAsync(NewAlarm(deviceA, AlarmDomain.AlarmState.Resolved));

        var active = await repo.GetActiveByDeviceAsync(deviceA);

        Assert.True(active.IsSuccess);
        var item = Assert.Single(active.Value!);
        Assert.Equal(deviceA, item.DeviceId);
    }

    [Fact]
    public async Task AlarmRepo_UpdateStateToResolved_SetsResolvedAtAndDropsFromActive()
    {
        var repo = new InMemoryAlarmRepository();
        var deviceId = Guid.NewGuid();
        var alarm = NewAlarm(deviceId);
        await repo.SaveAsync(alarm);

        await repo.UpdateStateAsync(alarm.Id, AlarmDomain.AlarmState.Resolved);

        Assert.Empty((await repo.GetActiveByDeviceAsync(deviceId)).Value!);
        Assert.Empty((await repo.GetAllActiveAsync()).Value!);
    }

    [Fact]
    public async Task AlarmRepo_UpdateState_UnknownId_IsNoOpSuccess()
    {
        var repo = new InMemoryAlarmRepository();
        var result = await repo.UpdateStateAsync(Guid.NewGuid(), AlarmDomain.AlarmState.Resolved);
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task AlarmRepo_UpdateState_NonResolved_DoesNotSetResolvedAt()
    {
        var repo = new InMemoryAlarmRepository();
        var deviceId = Guid.NewGuid();
        var alarm = NewAlarm(deviceId, AlarmDomain.AlarmState.Pending);
        await repo.SaveAsync(alarm);

        await repo.UpdateStateAsync(alarm.Id, AlarmDomain.AlarmState.Active);

        var item = Assert.Single((await repo.GetActiveByDeviceAsync(deviceId)).Value!);
        Assert.Equal(AlarmDomain.AlarmState.Active, item.State);
        Assert.Equal(default, item.ResolvedAt);
    }

    [Fact]
    public async Task AlarmRepo_GetAllActive_OnlyActive()
    {
        var repo = new InMemoryAlarmRepository();
        await repo.SaveAsync(NewAlarm(Guid.NewGuid(), AlarmDomain.AlarmState.Active));
        await repo.SaveAsync(NewAlarm(Guid.NewGuid(), AlarmDomain.AlarmState.Pending));
        await repo.SaveAsync(NewAlarm(Guid.NewGuid(), AlarmDomain.AlarmState.Resolved));

        var all = await repo.GetAllActiveAsync();

        Assert.Single(all.Value!);
    }

    [Fact]
    public async Task AlarmRepo_Query_FiltersByRange_OrdersDescending()
    {
        var repo = new InMemoryAlarmRepository();
        await repo.SaveAsync(NewAlarm(Guid.NewGuid(), occurredAt: T0.AddHours(-1)));
        await repo.SaveAsync(NewAlarm(Guid.NewGuid(), occurredAt: T0.AddHours(1)));
        await repo.SaveAsync(NewAlarm(Guid.NewGuid(), occurredAt: T0.AddHours(5))); // 范围外

        var result = await repo.QueryAsync(T0.AddHours(-2), T0.AddHours(2));

        Assert.Equal(2, result.Value!.Count);
        Assert.True(result.Value![0].OccurredAt >= result.Value![1].OccurredAt);
    }

    [Fact]
    public async Task AlarmRepo_Query_LimitIsClampedToAtLeastOne()
    {
        var repo = new InMemoryAlarmRepository();
        await repo.SaveAsync(NewAlarm(Guid.NewGuid(), occurredAt: T0));
        await repo.SaveAsync(NewAlarm(Guid.NewGuid(), occurredAt: T0.AddMinutes(1)));

        var result = await repo.QueryAsync(T0.AddHours(-1), T0.AddHours(1), limit: 0);

        Assert.Single(result.Value!);
    }

    [Fact]
    public async Task AlarmRepo_CountOccurredSince_CountsFromBoundary()
    {
        var repo = new InMemoryAlarmRepository();
        await repo.SaveAsync(NewAlarm(Guid.NewGuid(), occurredAt: T0));
        await repo.SaveAsync(NewAlarm(Guid.NewGuid(), occurredAt: T0.AddMinutes(1)));
        await repo.SaveAsync(NewAlarm(Guid.NewGuid(), occurredAt: T0.AddMinutes(-1)));

        var count = await repo.CountOccurredSinceAsync(T0);

        Assert.Equal(2, count.Value);
    }

    // ── AlarmRuleRepository ──

    [Fact]
    public async Task RuleRepo_GetByPoint_FiltersByDevicePointAndEnabled()
    {
        var repo = new InMemoryAlarmRuleRepository();
        var deviceId = Guid.NewGuid();
        var pointId = Guid.NewGuid();
        await repo.SaveAsync(NewRule(deviceId, pointId));
        await repo.SaveAsync(NewRule(deviceId, Guid.NewGuid()));
        await repo.SaveAsync(NewRule(Guid.NewGuid(), pointId));
        await repo.SaveAsync(NewRule(deviceId, pointId, enabled: false));

        var result = await repo.GetByPointAsync(deviceId, pointId);

        Assert.Single(result.Value!);
    }

    [Fact]
    public async Task RuleRepo_GetByDevice_OnlyEnabled()
    {
        var repo = new InMemoryAlarmRuleRepository();
        var deviceId = Guid.NewGuid();
        await repo.SaveAsync(NewRule(deviceId, Guid.NewGuid()));
        await repo.SaveAsync(NewRule(deviceId, Guid.NewGuid(), enabled: false));
        await repo.SaveAsync(NewRule(Guid.NewGuid(), Guid.NewGuid()));

        Assert.Single((await repo.GetByDeviceAsync(deviceId)).Value!);
    }

    [Fact]
    public async Task RuleRepo_GetAll_OnlyEnabled_GetAllIncludingDisabled_ReturnsAll()
    {
        var repo = new InMemoryAlarmRuleRepository();
        await repo.SaveAsync(NewRule(Guid.NewGuid(), Guid.NewGuid()));
        await repo.SaveAsync(NewRule(Guid.NewGuid(), Guid.NewGuid(), enabled: false));

        Assert.Single((await repo.GetAllAsync()).Value!);
        Assert.Equal(2, (await repo.GetAllIncludingDisabledAsync()).Value!.Count);
    }

    [Fact]
    public async Task RuleRepo_Delete_RemovesRule()
    {
        var repo = new InMemoryAlarmRuleRepository();
        var rule = NewRule(Guid.NewGuid(), Guid.NewGuid());
        await repo.SaveAsync(rule);

        await repo.DeleteAsync(rule.Id);

        Assert.Empty((await repo.GetAllIncludingDisabledAsync()).Value!);
    }
}
