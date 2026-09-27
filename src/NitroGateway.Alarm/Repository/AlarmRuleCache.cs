using NitroGateway.Primitives.Caching;
using NitroGateway.Shared;

namespace NitroGateway.Alarm.Repository;

/// <summary>
/// 告警规则缓存：TTL + 写失效的**机制**由 <see cref="TtlCache{TKey,TValue}"/> 统一承载，
/// 本类只保留领域策略（TTL 30s、加载委托指向内层仓储、写成功后失效）。
/// </summary>
public sealed class AlarmRuleCache : IDisposable
{
    /// <summary>单值缓存的固定键（启用规则全量为唯一缓存对象）。</summary>
    private const string CacheKey = "all";

    private readonly TtlCache<string, IReadOnlyList<Domain.AlarmRule>> _cache;

    /// <summary>
    /// 创建缓存。
    /// </summary>
    /// <param name="ttl">新鲜度兜底窗口；缺省 30 秒。测试可注入 <see cref="TimeSpan.Zero"/>
    /// 让缓存恒失效（每次读取都走 loader），或注入极大值关闭 TTL 兜底。</param>
    public AlarmRuleCache(TimeSpan? ttl = null)
        => _cache = new TtlCache<string, IReadOnlyList<Domain.AlarmRule>>(ttl ?? TimeSpan.FromSeconds(30));

    /// <summary>
    /// 取缓存；缓存失效/过期时经 <paramref name="loader"/> 重建。
    /// 加载成功落缓存并返回结果；失败原样返回 Failure（不落缓存、下次重试）。
    /// </summary>
    /// <param name="loader">缓存未命中时的数据源（由调用方注入内层仓储的 GetAllAsync）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回启用规则列表；失败返回内层错误。</returns>
    public Task<OperationResult<IReadOnlyList<Domain.AlarmRule>>> GetOrLoadAsync(
        Func<CancellationToken, Task<OperationResult<IReadOnlyList<Domain.AlarmRule>>>> loader,
        CancellationToken ct = default)
        => _cache.GetOrLoadAsync(CacheKey, loader, ct);

    /// <summary>
    /// 失效缓存：下一次读取强制重载。
    /// 由仓储装饰器在 SaveAsync/DeleteAsync 成功后调用，保证规则变更立即可见。
    /// </summary>
    public void Invalidate() => _cache.Invalidate();

    /// <summary>释放刷新闸门（Singleton，宿主关闭时调用）。</summary>
    public void Dispose() => _cache.Dispose();
}
