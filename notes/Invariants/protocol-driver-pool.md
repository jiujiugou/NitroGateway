# ProtocolDriverPool 并发模型

## 范围

验证 ProtocolDriverPool 的并发一致性与 driver 生命周期。

包含：
GetOrCreate / Evict / Dispose / driver 生命周期。

不包含：
driver 内部并发、factory 线程安全、调用方裸借用期间的生命周期保证。

## 并发不变量

| ID | 不变量 |
|---|---|
| I1 | 同 device + key 并发 GetOrCreate 只创建一个 driver |
| I2 | 每设备最多一个 pool entry |
| I3 | 每个 driver 最多释放一次 |
| I4 | Dispose 幂等 |
| I5 | Create 失败不留下半装 entry |
| I6 | driver 释放不持有 pool lock |
| I7 | driver 释放后调用不得进入 driver |

## Coyote

正例：
I1 / I3 / I4 / I5 / I6 / I7

负控：
UnlockedPool
LockedCheckThenActPool
LeakyEvictPool
NonIdempotentDisposePool
DisposeInLockPool
NoGuardDriver
DrainDriver

要求：

Positive → bugs = 0
Negative → bugs > 0

## 已接受的边界

裸借用不提供使用期租约。

同步 Dispose 不排水，在途调用允许失败；
DisposeAsync 等待在途操作完成。

详见 ADR-077 / ADR-078。