using System.Collections.Concurrent;
using NitroGateway.Shared;

namespace NitroGateway.Alarm.Repository;

/// <summary>
/// 内存告警规则存储（占位实现）。
/// 后续由 Persistence.Sqlite 提供 SqliteAlarmRuleRepository 替换。
/// </summary>
internal sealed class InMemoryAlarmRuleRepository : IAlarmRuleRepository
{
    private readonly ConcurrentDictionary<Guid, Domain.AlarmRule> _rules = new();

    public Task<OperationResult<IReadOnlyList<Domain.AlarmRule>>> GetByPointAsync(
        Guid deviceId, Guid pointId, CancellationToken ct = default)
    {
        var rules = _rules.Values
            .Where(r => r.DeviceId == deviceId && r.PointId == pointId && r.Enabled)
            .ToList();
        return Task.FromResult<OperationResult<IReadOnlyList<Domain.AlarmRule>>>(rules);
    }

    public Task<OperationResult<IReadOnlyList<Domain.AlarmRule>>> GetAllAsync(CancellationToken ct = default)
    {
        var rules = _rules.Values.Where(r => r.Enabled).ToList();
        return Task.FromResult<OperationResult<IReadOnlyList<Domain.AlarmRule>>>(rules);
    }

    public Task<OperationResult> SaveAsync(Domain.AlarmRule rule, CancellationToken ct = default)
    {
        _rules[rule.Id] = rule;
        return Task.FromResult(OperationResult.Success());
    }

    public Task<OperationResult> DeleteAsync(Guid ruleId, CancellationToken ct = default)
    {
        _rules.TryRemove(ruleId, out _);
        return Task.FromResult(OperationResult.Success());
    }
}
