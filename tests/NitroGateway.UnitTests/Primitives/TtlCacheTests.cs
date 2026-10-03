using NitroGateway.Primitives.Caching;
using NitroGateway.Shared;
using Xunit;

namespace NitroGateway.UnitTests.Primitives;

/// <summary>
/// 通用 TTL 缓存机制测试：锁定"原子加载一次、TTL 过期、失效、失败不缓存"的机制契约，
/// 保证 AlarmRuleCache / DeviceSnapshotCache 迁移到同一机制后行为等价。
/// </summary>
public class TtlCacheTests
{
    private static readonly TimeSpan LongTtl = TimeSpan.FromMinutes(5);

    private static Task<OperationResult<int>> Load(ref int calls, int value)
    {
        Interlocked.Increment(ref calls);
        return Task.FromResult(OperationResult<int>.Success(value));
    }

    [Fact]
    public async Task GetOrLoad_MissThenHit_LoadsOnce()
    {
        var cache = new TtlCache<string, int>(LongTtl);
        var calls = 0;

        var first = await cache.GetOrLoadAsync("k", _ => Load(ref calls, 42));
        var second = await cache.GetOrLoadAsync("k", _ => Load(ref calls, 99));

        Assert.Equal(42, first.Value);
        Assert.Equal(42, second.Value);
        // 热路径命中缓存：内层加载只发生一次
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task GetOrLoad_ConcurrentMiss_ExecutesFactoryOnce()
    {
        // check-then-act 不变量：N 个并发调用只允许一个穿透到工厂。
        var cache = new TtlCache<string, int>(LongTtl);
        var calls = 0;

        async Task<OperationResult<int>> Factory(CancellationToken ct)
        {
            Interlocked.Increment(ref calls);
            await Task.Delay(50, ct);
            return OperationResult<int>.Success(7);
        }

        var tasks = Enumerable.Range(0, 16)
            .Select(_ => cache.GetOrLoadAsync("k", Factory))
            .ToArray();
        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, calls);
        Assert.All(results, r => Assert.Equal(7, r.Value));
    }

    [Fact]
    public async Task GetOrLoad_TtlExpiry_Reloads()
    {
        var cache = new TtlCache<string, int>(TimeSpan.FromMilliseconds(1));
        var calls = 0;

        await cache.GetOrLoadAsync("k", _ => Load(ref calls, 1));
        await Task.Delay(TimeSpan.FromMilliseconds(15));
        await cache.GetOrLoadAsync("k", _ => Load(ref calls, 2));

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Invalidate_ForcesReload()
    {
        var cache = new TtlCache<string, int>(LongTtl);
        var calls = 0;

        await cache.GetOrLoadAsync("k", _ => Load(ref calls, 1));
        cache.Invalidate();
        var reloaded = await cache.GetOrLoadAsync("k", _ => Load(ref calls, 2));

        Assert.Equal(2, reloaded.Value);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Invalidate_Key_OnlyAffectsThatKey()
    {
        var cache = new TtlCache<string, int>(LongTtl);
        var callsA = 0;
        var callsB = 0;

        await cache.GetOrLoadAsync("a", _ => Load(ref callsA, 1));
        await cache.GetOrLoadAsync("b", _ => Load(ref callsB, 1));

        cache.Invalidate("a");
        await cache.GetOrLoadAsync("a", _ => Load(ref callsA, 2));
        await cache.GetOrLoadAsync("b", _ => Load(ref callsB, 2));

        Assert.Equal(2, callsA); // a 被失效 → 重载
        Assert.Equal(1, callsB); // b 不受影响
    }

    [Fact]
    public async Task GetOrLoad_Failure_NotCached_RetriesNextCall()
    {
        var cache = new TtlCache<string, int>(LongTtl);
        var calls = 0;

        var failed = await cache.GetOrLoadAsync("k", _ =>
        {
            calls++;
            return Task.FromResult(OperationResult<int>.Failure(OperationalError.Storage("模拟加载失败")));
        });
        var retried = await cache.GetOrLoadAsync("k", _ => Load(ref calls, 5));

        Assert.True(failed.IsFailure);
        Assert.Equal(5, retried.Value);
        // 失败不落缓存：下一次读取重新走工厂
        Assert.Equal(2, calls);
    }

    [Fact]
    public void Constructor_NegativeTtl_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TtlCache<string, int>(TimeSpan.FromSeconds(-1)));
    }
}
