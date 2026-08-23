# ADR-017: Forwarder 模块二轮 Code Review

- 日期: 2026-08-09
- 状态: 全部条目已修复（2026-08-09）——P1-1 + P2×2 + P3×3
- 范围: src/NitroGateway.Forwarder 全部文件 + 依赖（SqliteForwardBuffer / MqttClientWrapper / MqttHostedService / GatewayLifecycle / StatusController / Program.cs）+ 相关测试

## 处理记录（2026-08-09）

- P1-1 积压查询无异常防护：`SqliteForwardBuffer.GetCountAsync` 改为与其他方法一致 catch+分类（取消仍抛出，DB 故障 Warning + 按 0 处理）；`ForwarderEngine.RunRoundAsync` 再加接口级兜底 catch 跳过本轮。红绿：`GetCountAsync_OnDbError_ReturnsZeroInsteadOfThrowing`、`BacklogQueryFailure_DoesNotStopEngine` 先红后绿
- P2-1 空轮指标不刷新：空轮分支补 `BufferBacklog.Set(0)` + ThrottleBatchSize；非空轮 gauge 在提交后统一刷新。红绿：`EmptyRound_ResetsBacklogGaugeToZero` 先红后绿
- P2-2 取消被当失败：`Forwarder.cs` 新增单独 `catch (OperationCanceledException)`——不 MarkFailed/不计数/不收紧节流，置 Error 后 rethrow 交停机路径；提交前加 `!ct.IsCancellationRequested` 守卫，避免取消瞬间误报"提交失败"。红绿：`ForwardBatchAsync_Cancelled_DoesNotMarkFailedAndRethrows` 先红后绿
- P3-1 同步 Count 查库：`Forwarder.cs` 指标改 `await _buffer.GetCountAsync(ct)`（`RoundWithPendingFailure_UpdatesGaugeToRemainingCount` / `RoundWithSuccess_CommitsAndGaugeReflectsEmptyBuffer` 覆盖）
- P3-2 intervalMs 无校验：`AddNitroForwarder` 注册时校验并抛 `ArgumentOutOfRangeException(nameof(intervalMs))`（`ForwarderRegistrationTests` 3 例）
- P3-3 StatusController 客户端断开 500：`SystemStatus` 捕获 OCE 按 0 收尾
- 测试基建：新增 `ForwarderCollection`（DisableParallelization）——BufferBacklog 是进程级全局 Gauge，Forwarder 相关测试类并入串行集合，保证指标精确值断言确定性
- 验证: build 0 错误；UnitTests 215 通过（211+4）；IntegrationTests 31 通过（26+5）
