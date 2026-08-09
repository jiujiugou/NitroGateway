using Xunit;

namespace NitroGateway.IntegrationTests;

/// <summary>
/// Forwarder 相关测试串行集合（ADR-017 P2-1 指标测试引入）。
/// 原因：NitroMetrics.BufferBacklog 是进程级全局 Gauge，多个测试类并发执行时
/// 互相覆盖精确值断言（仓库已有死信指标精确值断言在并行下失败的先例）。
/// 该集合内测试串行运行，且不与其它集合并行，保证指标断言确定性。
/// </summary>
[CollectionDefinition("Forwarder", DisableParallelization = true)]
public sealed class ForwarderCollection;
