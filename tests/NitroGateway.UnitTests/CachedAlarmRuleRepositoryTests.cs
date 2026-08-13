using NitroGateway.Alarm.Domain;
using NitroGateway.Alarm.Repository;
using NitroGateway.Shared;
using Xunit;
using AlarmDomain = NitroGateway.Alarm.Domain;

namespace NitroGateway.UnitTests;

/// <summary>
/// CachedAlarmRuleRepository / AlarmRuleCache 测试（ADR-032 P1-2）：
/// 热路径规则读取经内存缓存（内层 GetAllAsync 只应加载一次），
/// 写成功失效缓存、加载失败不落缓存且下次重试、TTL 兜底强制重载。
/// </summary>
public class CachedAlarmRuleRepositoryTests
{
    /// <summary>
    /// 内存假仓储：模拟 SqliteAlarmRuleRepository 的语义（GetAll 仅返回 Enabled），
    /// 用 GetAllCallCount 统计内层实际查询次数，用于断言缓存是否生效。
    /// </summary>
    private sealed class FakeRuleRepository : IAlarmRuleRepository
    {
        private readonly Dictionary<Guid, AlarmDomain.AlarmRule> _rules = new();

        /// <summary>GetAllAsync 被调用次数（= 内层 DB 查询次数）。</summary>
        public int GetAllCallCount { get; private set; }

        /// <summary>GetAllIncludingDisabledAsync 被调用次数（ADR-043：管理页直读路径）。</summary>
        public int GetAllIncludingDisabledCallCount { get; private set; }

        /// <summary>置 true 时下一次 GetAllAsync 返回失败（模拟 DB 故障），随后自动复位。</summary>
        public bool FailNextGetAll { get; set; }

        public Task<OperationResult<IReadOnlyList<AlarmDomain.AlarmRule>>> GetByPointAsync(
            Guid deviceId, Guid pointId, CancellationToken ct = default)
            => Task.FromResult<OperationResult<IReadOnlyList<AlarmDomain.AlarmRule>>>(
                _rules.Values
                    .Where(r => r.DeviceId == deviceId && r.PointId == pointId && r.Enabled)
                    .ToList());

        public Task<OperationResult<IReadOnlyList<AlarmDomain.AlarmRule>>> GetByDeviceAsync(
            Guid deviceId, CancellationToken ct = default)
            => Task.FromResult<OperationResult<IReadOnlyList<AlarmDomain.AlarmRule>>>(
                _rules.Values
                    .Where(r => r.DeviceId == deviceId && r.Enabled)
                    .ToList());

        public Task<OperationResult<IReadOnlyList<AlarmDomain.AlarmRule>>> GetAllAsync(
            CancellationToken ct = default)
        {
            GetAllCallCount++;
            if (FailNextGetAll)
            {
                FailNextGetAll = false;
                return Task.FromResult<OperationResult<IReadOnlyList<AlarmDomain.AlarmRule>>>(
                    OperationalError.Storage("模拟内层加载失败"));
            }

            return Task.FromResult<OperationResult<IReadOnlyList<AlarmDomain.AlarmRule>>>(
                _rules.Values.Where(r => r.Enabled).ToList());
        }

        public Task<OperationResult<IReadOnlyList<AlarmDomain.AlarmRule>>> GetAllIncludingDisabledAsync(
            CancellationToken ct = default)
        {
            GetAllIncludingDisabledCallCount++;
            return Task.FromResult<OperationResult<IReadOnlyList<AlarmDomain.AlarmRule>>>(
                _rules.Values.ToList());
        }

        public Task<OperationResult> SaveAsync(AlarmDomain.AlarmRule rule, CancellationToken ct = default)
        {
            _rules[rule.Id] = rule;
            return Task.FromResult(OperationResult.Success());
        }

        public Task<OperationResult> DeleteAsync(Guid ruleId, CancellationToken ct = default)
        {
            _rules.Remove(ruleId);
            return Task.FromResult(OperationResult.Success());
        }
    }

    private static AlarmDomain.AlarmRule NewRule(Guid deviceId, Guid pointId, bool enabled = true) => new()
    {
        Id = Guid.NewGuid(),
        DeviceId = deviceId,
        PointId = pointId,
        Operator = ">",
        Threshold = 70,
        Severity = AlarmSeverity.Warning,
        DurationSeconds = 0,
        Enabled = enabled
    };

    [Fact]
    public async Task GetByDeviceAsync_FirstCallLoads_SecondCallServedFromCache()
    {
        var inner = new FakeRuleRepository();
        var repo = new CachedAlarmRuleRepository(new AlarmRuleCache(), inner);
        var deviceId = Guid.NewGuid();
        await inner.SaveAsync(NewRule(deviceId, Guid.NewGuid()));

        var first = await repo.GetByDeviceAsync(deviceId);
        var second = await repo.GetByDeviceAsync(deviceId);

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        Assert.Single(second.Value!);
        // 热路径：两次读取只触发一次内层查询
        Assert.Equal(1, inner.GetAllCallCount);
    }

    [Fact]
    public async Task GetByDeviceAsync_FiltersByDeviceAndEnabled()
    {
        var inner = new FakeRuleRepository();
        var repo = new CachedAlarmRuleRepository(new AlarmRuleCache(), inner);
        var deviceA = Guid.NewGuid();
        var deviceB = Guid.NewGuid();
        await inner.SaveAsync(NewRule(deviceA, Guid.NewGuid()));
        await inner.SaveAsync(NewRule(deviceB, Guid.NewGuid()));
        await inner.SaveAsync(NewRule(deviceA, Guid.NewGuid(), enabled: false));

        var result = await repo.GetByDeviceAsync(deviceA);

        var item = Assert.Single(result.Value!);
        Assert.Equal(deviceA, item.DeviceId);
    }

    [Fact]
    public async Task GetByPointAsync_FiltersByPoint()
    {
        var inner = new FakeRuleRepository();
        var repo = new CachedAlarmRuleRepository(new AlarmRuleCache(), inner);
        var deviceId = Guid.NewGuid();
        var pointA = Guid.NewGuid();
        var pointB = Guid.NewGuid();
        await inner.SaveAsync(NewRule(deviceId, pointA));
        await inner.SaveAsync(NewRule(deviceId, pointB));

        var result = await repo.GetByPointAsync(deviceId, pointA);

        var item = Assert.Single(result.Value!);
        Assert.Equal(pointA, item.PointId);
    }

    [Fact]
    public async Task SaveAsync_InvalidatesCache_NextReadSeesNewRule()
    {
        var inner = new FakeRuleRepository();
        var repo = new CachedAlarmRuleRepository(new AlarmRuleCache(), inner);
        var deviceId = Guid.NewGuid();
        await inner.SaveAsync(NewRule(deviceId, Guid.NewGuid()));

        await repo.GetByDeviceAsync(deviceId); // 加载进缓存
        var rule = NewRule(deviceId, Guid.NewGuid());
        await repo.SaveAsync(rule);

        var result = await repo.GetByDeviceAsync(deviceId);

        Assert.Equal(2, result.Value!.Count);
        Assert.Contains(result.Value!, r => r.Id == rule.Id);
        // 写失效后读取触发重载：内层查询共 2 次
        Assert.Equal(2, inner.GetAllCallCount);
    }

    [Fact]
    public async Task DeleteAsync_InvalidatesCache_NextReadExcludesDeleted()
    {
        var inner = new FakeRuleRepository();
        var repo = new CachedAlarmRuleRepository(new AlarmRuleCache(), inner);
        var deviceId = Guid.NewGuid();
        var rule = NewRule(deviceId, Guid.NewGuid());
        await inner.SaveAsync(rule);
        await repo.GetByDeviceAsync(deviceId); // 加载进缓存

        var del = await repo.DeleteAsync(rule.Id);
        Assert.True(del.IsSuccess);

        var result = await repo.GetByDeviceAsync(deviceId);

        Assert.Empty(result.Value!);
        Assert.Equal(2, inner.GetAllCallCount);
    }

    [Fact]
    public async Task LoadFailure_ReturnsFailure_AndRetriesNextCall()
    {
        var inner = new FakeRuleRepository();
        var repo = new CachedAlarmRuleRepository(new AlarmRuleCache(), inner);
        var deviceId = Guid.NewGuid();
        await inner.SaveAsync(NewRule(deviceId, Guid.NewGuid()));
        inner.FailNextGetAll = true;

        var failed = await repo.GetByDeviceAsync(deviceId);
        var retried = await repo.GetByDeviceAsync(deviceId);

        Assert.True(failed.IsFailure);
        Assert.True(retried.IsSuccess);
        Assert.Single(retried.Value!);
        // 失败不落缓存：下一次读取重新走内层
        Assert.Equal(2, inner.GetAllCallCount);
    }

    [Fact]
    public async Task TtlExpiry_ForcesReload()
    {
        var inner = new FakeRuleRepository();
        var repo = new CachedAlarmRuleRepository(new AlarmRuleCache(TimeSpan.FromMilliseconds(1)), inner);
        var deviceId = Guid.NewGuid();
        await inner.SaveAsync(NewRule(deviceId, Guid.NewGuid()));

        await repo.GetByDeviceAsync(deviceId); // 首次加载
        await Task.Delay(TimeSpan.FromMilliseconds(10));
        await repo.GetByDeviceAsync(deviceId); // TTL 过期 → 强制重载

        Assert.Equal(2, inner.GetAllCallCount);
    }

    [Fact]
    public async Task CacheIsSharedAcrossDecoratorInstances()
    {
        // 进程内同一 Singleton 缓存被多个 scope 的装饰器共享：第二个装饰器读取不再触发内层查询
        var inner = new FakeRuleRepository();
        var cache = new AlarmRuleCache();
        var deviceId = Guid.NewGuid();
        await inner.SaveAsync(NewRule(deviceId, Guid.NewGuid()));

        var repoA = new CachedAlarmRuleRepository(cache, inner);
        var repoB = new CachedAlarmRuleRepository(cache, inner);

        await repoA.GetByDeviceAsync(deviceId);
        var result = await repoB.GetByDeviceAsync(deviceId);

        Assert.Single(result.Value!);
        Assert.Equal(1, inner.GetAllCallCount);
    }

    [Fact]
    public async Task GetAllIncludingDisabledAsync_ReturnsDisabledRules_AndBypassesCache()
    {
        // ADR-043：管理页读取含禁用规则，必须绕过只存启用规则的缓存直读内层——
        // 既能看到禁用规则，也不污染热路径缓存（GetAllCallCount 不增长）。
        var inner = new FakeRuleRepository();
        var repo = new CachedAlarmRuleRepository(new AlarmRuleCache(), inner);
        var deviceId = Guid.NewGuid();
        await inner.SaveAsync(NewRule(deviceId, Guid.NewGuid()));
        await inner.SaveAsync(NewRule(deviceId, Guid.NewGuid(), enabled: false));

        // 先触发一次热路径加载（进缓存）
        await repo.GetByDeviceAsync(deviceId);
        Assert.Equal(1, inner.GetAllCallCount);

        var result = await repo.GetAllIncludingDisabledAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value!.Count);
        Assert.Contains(result.Value!, r => !r.Enabled);
        // 管理页直读不失效缓存：热路径查询次数不变
        Assert.Equal(1, inner.GetAllCallCount);
        Assert.Equal(1, inner.GetAllIncludingDisabledCallCount);
    }
}
