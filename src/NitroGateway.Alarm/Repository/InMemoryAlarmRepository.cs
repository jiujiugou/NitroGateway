using System.Collections.Concurrent;
using NitroGateway.Shared;

namespace NitroGateway.Alarm.Repository;

/// <summary>
/// 内存告警记录存储（占位实现）。
/// 后续由 Persistence.Sqlite 提供 SqliteAlarmRepository 替换。
/// </summary>
internal sealed class InMemoryAlarmRepository : IAlarmRepository
{
    private readonly ConcurrentDictionary<Guid, Domain.Alarm> _alarms = new();

    public Task<OperationResult> SaveAsync(Domain.Alarm alarm, CancellationToken ct = default)
    {
        _alarms[alarm.Id] = alarm;
        return Task.FromResult(OperationResult.Success());
    }

    public Task<OperationResult> UpdateStateAsync(Guid alarmId, Domain.AlarmState state, CancellationToken ct = default)
    {
        if (_alarms.TryGetValue(alarmId, out var alarm))
        {
            alarm.State = state;
            if (state == Domain.AlarmState.Resolved)
                alarm.ResolvedAt = DateTime.UtcNow;
        }
        return Task.FromResult(OperationResult.Success());
    }

    public Task<OperationResult<IReadOnlyList<Domain.Alarm>>> GetActiveByDeviceAsync(
        Guid deviceId, CancellationToken ct = default)
    {
        var active = _alarms.Values
            .Where(a => a.DeviceId == deviceId && a.State == Domain.AlarmState.Active)
            .ToList();
        return Task.FromResult<OperationResult<IReadOnlyList<Domain.Alarm>>>(active);
    }

    public Task<OperationResult<IReadOnlyList<Domain.Alarm>>> GetAllActiveAsync(CancellationToken ct = default)
    {
        var active = _alarms.Values
            .Where(a => a.State == Domain.AlarmState.Active)
            .ToList();
        return Task.FromResult<OperationResult<IReadOnlyList<Domain.Alarm>>>(active);
    }

    public Task<OperationResult<IReadOnlyList<Domain.Alarm>>> QueryAsync(
        DateTime from, DateTime to, CancellationToken ct = default)
    {
        var results = _alarms.Values
            .Where(a => a.OccurredAt >= from && a.OccurredAt <= to)
            .OrderByDescending(a => a.OccurredAt)
            .ToList();
        return Task.FromResult<OperationResult<IReadOnlyList<Domain.Alarm>>>(results);
    }
}
