using System.Collections.Concurrent;
using NitroGateway.Shared;

namespace NitroGateway.Primitives.Caching;

/// <summary>
/// 通用 TTL 缓存机制：把"带闸门的缓存 + 双检 + TTL + 失效"的**机制**收敛到一处，
/// 领域只提供策略参数（TTL、失效触发时机）与加载委托。
/// <para><b>原子加载：</b><see cref="GetOrLoadAsync"/> 未命中时经闸门串行化并二次检查，
/// 保证同一 key 的并发加载只执行一次工厂（消除 check-then-act）。</para>
/// <para><b>失败不落缓存：</b>工厂返回失败时原样返回，不写入缓存，下次读取重试。</para>
/// <para><b>失效触发是策略：</b>领域在保存/删除成功后调用 <see cref="Invalidate(TKey)"/> 或
/// <see cref="Invalidate()"/>，机制不关心何时失效。</para>
/// </summary>
/// <typeparam name="TKey">缓存键</typeparam>
/// <typeparam name="TValue">缓存值</typeparam>
public sealed class TtlCache<TKey, TValue> : IDisposable where TKey : notnull
{
    /// <summary>刷新闸门：同一时刻只允许一个加载者重建缓存。</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>新鲜度兜底窗口：超过该时长即使无写事件也强制重载。</summary>
    private readonly TimeSpan _ttl;

    /// <summary>已加载条目（ConcurrentDictionary 保证锁外快路径读取安全）。</summary>
    private readonly ConcurrentDictionary<TKey, Entry> _entries = new();

    /// <summary>创建 TTL 缓存。</summary>
    /// <param name="ttl">新鲜度兜底窗口；<see cref="TimeSpan.Zero"/> 表示恒失效（每次读取都走工厂）。</param>
    public TtlCache(TimeSpan ttl)
    {
        if (ttl < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(ttl), ttl, "TTL 不能为负。");

        _ttl = ttl;
    }

    /// <summary>
    /// 取缓存；缓存失效/过期/未命中时经 <paramref name="factory"/> 加载。
    /// 加载成功落缓存并返回结果；失败原样返回 Failure（不落缓存，下次重试）。
    /// </summary>
    /// <param name="key">缓存键。</param>
    /// <param name="factory">缓存未命中时的数据源。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回缓存值；失败返回工厂错误。</returns>
    public async Task<OperationResult<TValue>> GetOrLoadAsync(
        TKey key,
        Func<CancellationToken, Task<OperationResult<TValue>>> factory,
        CancellationToken ct = default)
    {
        // 快路径：缓存新鲜直接返回，热路径（每秒多次）零锁零 IO
        if (TryGetFresh(key, out var cached))
            return OperationResult<TValue>.Success(cached!);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // 双检：等待闸门期间可能已被其他线程加载
            if (TryGetFresh(key, out cached))
                return OperationResult<TValue>.Success(cached!);

            var result = await factory(ct).ConfigureAwait(false);
            if (result.IsFailure)
                return result;

            if (result.Value is not null)
                _entries[key] = new Entry(result.Value, DateTimeOffset.UtcNow);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>失效指定 key：下一次读取强制重载（由领域在写成功后调用）。</summary>
    public void Invalidate(TKey key) => _entries.TryRemove(key, out _);

    /// <summary>失效全部条目：下一次读取强制重载。</summary>
    public void Invalidate() => _entries.Clear();

    /// <summary>判定指定 key 是否新鲜：已加载、值非空、且未超过 TTL 兜底窗口。</summary>
    /// <param name="key">缓存键。</param>
    /// <param name="value">新鲜时的缓存内容；否则为默认值。</param>
    private bool TryGetFresh(TKey key, out TValue? value)
    {
        if (_entries.TryGetValue(key, out var entry)
            && entry.Value is not null
            && DateTimeOffset.UtcNow - entry.LoadedAt < _ttl)
        {
            value = entry.Value;
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>释放刷新闸门（Singleton，宿主关闭时调用）。</summary>
    public void Dispose() => _gate.Dispose();

    /// <summary>缓存条目（不可变）。</summary>
    private sealed record Entry(TValue? Value, DateTimeOffset LoadedAt);
}
