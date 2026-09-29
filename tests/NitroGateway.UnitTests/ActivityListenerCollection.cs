using Xunit;

namespace NitroGateway.UnitTests;

/// <summary>
/// 进程级 <see cref="System.Diagnostics.ActivityListener"/> 相关测试的串行化集合。
/// <para>
/// <c>ActivitySource.AddActivityListener</c> 是进程级全局副作用：多个测试类并行时，
/// 一个类的监听器会捕获到另一个类在同一 <c>GatewayActivitySource</c> 上启动的 activity
/// （典型症状：断言"恰好 1 个 Forward activity"偶发捕获到 2~3 个）。
/// 把这类测试放进同一条禁用并行的集合，保证监听窗口内只有本集合的 activity。
/// </para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ActivityListenerCollection
{
    /// <summary>集合名（供 <c>[Collection(...)]</c> 引用）。</summary>
    public const string Name = "ActivityListener";
}
