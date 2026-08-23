# ADR-001: Forwarder 数据可靠性问题清单

- 日期: 2026-08-06
- 状态: 全部条目已处理（2026-08-07）——P0/P1、P2-7~P2-10、P2-11、P3-12、P3-13 已修复；P3-14 v1 接受不修
- 用途: 供后续 agent 直接使用，避免重复扫描；修复后在代码加注释并删除本清单对应条目

## 处理记录（2026-08-07）

- P2-11 MarkFailedAsync 合并为 1 次 UPDATE（CASE 重试计数+超限进死信）+ 1 次 SELECT 判断，3 次往返 → 2 次；SqliteForwardBufferTests.MarkFailed_OverMaxRetries_MovesToDeadLetter 验证
- P3-12 ForwarderEngine 改为 do-while：首轮立即执行再进 PeriodicTimer 循环；新增 FirstRound_RunsImmediately_WithoutWaitingFullInterval 测试
- P3-13 IForwardBuffer 新增 GetCountAsync（接口只增不删），SqliteForwardBuffer 实现 ExecuteScalarAsync；ForwarderEngine/StatusController 改走异步；Count 同步属性保留兼容并注释；新增 GetCountAsync_ReturnsPendingCount_ExcludesDeadLetters 测试
- P3-14 节流全局共享 v1 单 Broker 可接受，不修；ForwardingThrottle 类注释记录该决策
