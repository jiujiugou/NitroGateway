# ADR-017: Forwarder 模块二轮 Code Review 决策

- 日期: 2026-08-09 | 状态: 已实施

## Context

Forwarder 二轮 review 发现：积压查询无异常防护、空轮指标不刷新、取消被当失败、同步 Count 查库、intervalMs 无校验、StatusController 客户端断开 500。

## Decision

- D1 积压查询异常防护：SqliteForwardBuffer.GetCountAsync catch+分类（取消仍抛出，DB 故障 Warning + 按 0 处理）；ForwarderEngine.RunRoundAsync 接口级兜底 catch 跳过本轮，DB 故障不停止转发引擎。
- D2 空轮指标刷新：空轮分支补 BufferBacklog.Set(0) + ThrottleBatchSize；非空轮 gauge 在提交后统一刷新。
- D3 取消不当作失败：Forwarder 新增单独 catch (OperationCanceledException)——不 MarkFailed/不计数/不收紧节流，置 Error 后 rethrow 交停机路径；提交前加 !ct.IsCancellationRequested 守卫，避免取消瞬间误报"提交失败"。
- D4 指标改异步计数：Forwarder.cs 改 await _buffer.GetCountAsync(ct)。
- D5 intervalMs 校验：AddNitroForwarder 注册时校验并抛 ArgumentOutOfRangeException(nameof(intervalMs))。
- D6 StatusController 客户端断开 500：SystemStatus 捕获 OCE 按 0 收尾。

## Alternatives

- D3 备选：把取消当失败 MarkFailed（简单，但取消瞬间会误标失败并收紧节流）。

## Rationale

- 转发引擎不因 DB 瞬时故障停止；空轮/取消场景指标与语义正确；异步计数避免同步查库阻塞；配置错误启动即报错。

## Consequences

- DB 故障时引擎跳过本轮继续运行；空轮不再残留旧积压读数；停机取消不再误标失败；积压指标反映真实缓冲量。
