using NitroGateway.Shared;

namespace NitroGateway.Alarm.Repository;

/// <summary>
/// 告警规则仓储缓存装饰器（ADR-032 P1-2）。
/// 与 <see cref="IAlarmRuleRepository"/> 语义完全一致：读经 <see cref="AlarmRuleCache"/>
/// 走内存（热路径不再每设备每秒直查 DB），写透传内层仓储并在成功后失效缓存。
/// </summary>
/// <remarks>
/// <para><b>注册：</b>Scoped。构造注入 Singleton 缓存 + Scoped 内层仓储
/// （SqliteAlarmRuleRepository），内层 DbContext 的生命周期仍由调用方 scope 管理，
/// 与 AlarmHostedService 每事件建 scope 的模式一致。</para>
/// <para><b>语义对齐：</b>内层 GetAllAsync 只返回 Enabled 规则，本类基于该全量列表
/// 在内存按设备/点位过滤，结果与内层 GetByDeviceAsync/GetByPointAsync 一致
/// （设备 + 点位 + Enabled 三重过滤）。</para>
/// <para><b>一致性：</b>SaveAsync/DeleteAsync 成功才 Invalidate，失败不改缓存；
/// 规则量小，全量失效比按设备失效更简单且代价可忽略。</para>
/// </remarks>
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

    /// <inheritdoc />
    /// <remarks>
    /// ADR-043：管理页要展示/恢复禁用规则，而缓存只存启用规则（内层 GetAllAsync 已过滤
    /// Enabled），因此本方法绕过缓存直读内层仓储，不失效/不更新缓存；管理页为低频调用，
    /// 直读 DB 代价可忽略。
    /// </remarks>
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
