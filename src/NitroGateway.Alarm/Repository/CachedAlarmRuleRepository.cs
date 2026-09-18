using NitroGateway.Shared;

namespace NitroGateway.Alarm.Repository;

public sealed class CachedAlarmRuleRepository : IAlarmRuleRepository
{
    private readonly AlarmRuleCache _cache;
    private readonly IAlarmRuleRepository _inner;

    /// <summary>
    /// 创建装饰器。
    /// </summary>
    /// <param name="cache">进程级共享缓存（Singleton）。</param>
    /// <param name="inner">真实仓储（Scoped），负责首次加载与写透传。</param>
    public CachedAlarmRuleRepository(AlarmRuleCache cache, IAlarmRuleRepository inner)
    {
        _cache = cache;
        _inner = inner;
    }

    /// <inheritdoc />
    public Task<OperationResult<IReadOnlyList<Domain.AlarmRule>>> GetByPointAsync(
        Guid deviceId, Guid pointId, CancellationToken ct = default)
        => FilterAsync(deviceId, r => r.PointId == pointId, ct);

    /// <inheritdoc />
    public Task<OperationResult<IReadOnlyList<Domain.AlarmRule>>> GetByDeviceAsync(
        Guid deviceId, CancellationToken ct = default)
        => FilterAsync(deviceId, _ => true, ct);

    /// <inheritdoc />
    public Task<OperationResult<IReadOnlyList<Domain.AlarmRule>>> GetAllAsync(
        CancellationToken ct = default)
        => _cache.GetOrLoadAsync(_inner.GetAllAsync, ct);

    public Task<OperationResult<IReadOnlyList<Domain.AlarmRule>>> GetAllIncludingDisabledAsync(
        CancellationToken ct = default)
        => _inner.GetAllIncludingDisabledAsync(ct);

    /// <inheritdoc />
    public async Task<OperationResult> SaveAsync(Domain.AlarmRule rule, CancellationToken ct = default)
    {
        var result = await _inner.SaveAsync(rule, ct);
        if (result.IsSuccess)
            _cache.Invalidate();
        return result;
    }

    /// <inheritdoc />
    public async Task<OperationResult> DeleteAsync(Guid ruleId, CancellationToken ct = default)
    {
        var result = await _inner.DeleteAsync(ruleId, ct);
        if (result.IsSuccess)
            _cache.Invalidate();
        return result;
    }

    /// <summary>
    /// 从缓存全量启用规则中按设备 + 附加条件过滤。
    /// 内层 GetAllAsync 已过滤 Enabled，此处只需设备/点位条件，与内层查询语义等价。
    /// </summary>
    private async Task<OperationResult<IReadOnlyList<Domain.AlarmRule>>> FilterAsync(
        Guid deviceId, Func<Domain.AlarmRule, bool> extra, CancellationToken ct)
    {
        var all = await GetAllAsync(ct);
        if (all.IsFailure)
            return all;

        return OperationResult<IReadOnlyList<Domain.AlarmRule>>.Success(
            all.Value!.Where(r => r.DeviceId == deviceId && extra(r)).ToList());
    }
}
