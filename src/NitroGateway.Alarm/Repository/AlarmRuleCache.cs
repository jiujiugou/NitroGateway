using NitroGateway.Shared;

namespace NitroGateway.Alarm.Repository;

public sealed class AlarmRuleCache : IDisposable
{
    /// <summary>刷新闸门：同一时刻只允许一个加载者重建缓存。</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>新鲜度兜底窗口：超过该时长即使无写事件也强制重载。</summary>
    private readonly TimeSpan _ttl;

    /// <summary>已缓存的全部启用规则（null 表示尚未成功加载）。</summary>
    private IReadOnlyList<Domain.AlarmRule>? _rules;

    /// <summary>最近一次成功加载时间（UTC），用于 TTL 判定。</summary>
    private DateTimeOffset _loadedAt;

    /// <summary>失效标记：写路径置 true，下一次读取强制重载。初始 true（尚未加载）。</summary>
    private bool _invalidated = true;

    /// <summary>
    /// 创建缓存。
    /// </summary>
    /// <param name="ttl">新鲜度兜底窗口；缺省 30 秒。测试可注入 <see cref="TimeSpan.Zero"/>
    /// 让缓存恒失效（每次读取都走 loader），或注入极大值关闭 TTL 兜底。</param>
    public AlarmRuleCache(TimeSpan? ttl = null)
    {
        _ttl = ttl ?? TimeSpan.FromSeconds(30);
    }

    /// <summary>
    /// 取缓存；缓存失效/过期时经 <paramref name="loader"/> 重建。
    /// 加载成功落缓存并返回结果；失败原样返回 Failure（不落缓存、不标记已加载，下次重试）。
    /// </summary>
    /// <param name="loader">缓存未命中时的数据源（由调用方注入内层仓储的 GetAllAsync）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回启用规则列表；失败返回内层错误。</returns>
    public async Task<OperationResult<IReadOnlyList<Domain.AlarmRule>>> GetOrLoadAsync(
        Func<CancellationToken, Task<OperationResult<IReadOnlyList<Domain.AlarmRule>>>> loader,
        CancellationToken ct = default)
    {
        // 快路径：缓存新鲜直接返回，热路径（每秒多次）零锁零 IO
        if (IsFresh(out var cached))
            return OperationResult<IReadOnlyList<Domain.AlarmRule>>.Success(cached!);

        await _gate.WaitAsync(ct);
        try
        {
            // 双检：等待闸门期间可能已被其他线程刷新
            if (IsFresh(out cached))
                return OperationResult<IReadOnlyList<Domain.AlarmRule>>.Success(cached!);

            var result = await loader(ct);
            if (result.IsFailure)
                return result;

            _rules = result.Value;
            _loadedAt = DateTimeOffset.UtcNow;
            _invalidated = false;
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 失效缓存：下一次读取强制重载。
    /// 由仓储装饰器在 SaveAsync/DeleteAsync 成功后调用，保证规则变更立即可见。
    /// </summary>
    public void Invalidate() => _invalidated = true;

    /// <summary>
    /// 判定缓存是否新鲜：未失效、已成功加载、且未超过 TTL 兜底窗口。
    /// </summary>
    /// <param name="cached">当前缓存内容（可能为 null）。</param>
    private bool IsFresh(out IReadOnlyList<Domain.AlarmRule>? cached)
    {
        cached = _rules;
        return !_invalidated && cached is not null && DateTimeOffset.UtcNow - _loadedAt < _ttl;
    }

    /// <summary>释放刷新闸门（Singleton，宿主关闭时调用）。</summary>
    public void Dispose() => _gate.Dispose();
}
